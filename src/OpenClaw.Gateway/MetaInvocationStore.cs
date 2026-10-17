using System.Text.Json;
using System.Collections.Concurrent;
using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway;

public sealed class MetaInvocationStore : IHostedService, IDisposable
{
    private readonly string _path;
    private readonly string _lockPath;
    private readonly int _retentionDays;
    private readonly ConcurrentDictionary<Guid, FileStream> _activeLeases = new();

    public MetaInvocationStore(string storagePath, int retentionDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        if (retentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention must be at least one day.");

        _path = Path.GetFullPath(Path.Combine(storagePath, "meta-invocations.json"));
        _lockPath = _path + ".lock";
        _retentionDays = retentionDays;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
        => await MarkInterruptedInvocationsUncertainAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public async Task<MetaInvocationClaim> BeginOrGetAsync(
        string callerId,
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays);
        var removed = records.RemoveAll(record =>
            record.Status != MetaInvocationStatus.Running && record.CreatedAtUtc <= cutoff) > 0;

        var existingIndex = records.FindIndex(record =>
            string.Equals(record.CallerId, callerId, StringComparison.Ordinal) &&
            string.Equals(record.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            if (removed)
                await SaveAsync(records, cancellationToken);

            var existing = records[existingIndex];
            var kind = string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                ? MetaInvocationClaimKind.Existing
                : MetaInvocationClaimKind.Conflict;
            return new MetaInvocationClaim(kind, existing);
        }

        var record = new MetaInvocationRecord(
            callerId,
            idempotencyKey,
            requestHash,
            Guid.NewGuid(),
            MetaInvocationStatus.Running,
            Result: null,
            Error: null,
            DateTimeOffset.UtcNow);
        var lease = TryAcquireInvocationLease(record.InvocationId)
            ?? throw new IOException("Unable to acquire the meta invocation lease.");
        if (!_activeLeases.TryAdd(record.InvocationId, lease))
        {
            lease.Dispose();
            throw new InvalidOperationException("Meta invocation lease is already active in this process.");
        }

        try
        {
            records.Add(record);
            await SaveAsync(records, cancellationToken);
        }
        catch
        {
            ReleaseInvocationLease(record.InvocationId);
            throw;
        }

        return new MetaInvocationClaim(MetaInvocationClaimKind.Started, record);
    }

    public async Task CompleteAsync(
        string callerId,
        string idempotencyKey,
        string result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);
        var index = FindRecordIndex(records, callerId, idempotencyKey);
        if (index < 0)
            throw new InvalidOperationException("Meta invocation claim was not found.");

        var current = records[index];
        if (current.Status == MetaInvocationStatus.Completed && current.Result == result)
            return;
        if (current.Status is not (MetaInvocationStatus.Running or MetaInvocationStatus.AwaitingInput))
            throw new InvalidOperationException("Only a running or paused meta invocation can be completed.");

        records[index] = current with
        {
            Status = MetaInvocationStatus.Completed,
            Result = result,
            Error = null,
            Pause = null
        };
        await SaveAsync(records, cancellationToken);
        ReleaseInvocationLease(current.InvocationId);
    }

    /// <summary>
    /// Finishes an invocation with a terminal business failure (for example a denied approval).
    /// The run did not produce a result, but it is not "uncertain" either.
    /// </summary>
    public async Task CompleteWithErrorAsync(
        string callerId,
        string idempotencyKey,
        string errorCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);
        var index = FindRecordIndex(records, callerId, idempotencyKey);
        if (index < 0)
            throw new InvalidOperationException("Meta invocation claim was not found.");

        var current = records[index];
        if (current.Status == MetaInvocationStatus.Completed && current.Error == errorCode)
            return;
        if (current.Status is not (MetaInvocationStatus.Running or MetaInvocationStatus.AwaitingInput))
            throw new InvalidOperationException("Only a running or paused meta invocation can be completed.");

        records[index] = current with
        {
            Status = MetaInvocationStatus.Completed,
            Result = null,
            Error = errorCode,
            Pause = null
        };
        await SaveAsync(records, cancellationToken);
        ReleaseInvocationLease(current.InvocationId);
    }

    /// <summary>
    /// Records that the run stopped on a human-gated step and kept its durable checkpoint.
    /// Repeating the same pause (same reason and step) is an idempotent no-op.
    /// </summary>
    public async Task<MetaInvocationRecord> MarkAwaitingInputAsync(
        string callerId,
        string idempotencyKey,
        MetaInvocationPauseInfo pause,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentException.ThrowIfNullOrWhiteSpace(pause.StepId);

        using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);
        var index = FindRecordIndex(records, callerId, idempotencyKey);
        if (index < 0)
            throw new InvalidOperationException("Meta invocation claim was not found.");

        var current = records[index];
        if (current.Status == MetaInvocationStatus.Completed)
            throw new InvalidOperationException("A completed meta invocation cannot pause.");
        if (current.Status is not (MetaInvocationStatus.Running or MetaInvocationStatus.AwaitingInput))
            throw new InvalidOperationException("Only a running or paused meta invocation can await input.");

        var updated = current with
        {
            Status = MetaInvocationStatus.AwaitingInput,
            Error = null,
            Pause = pause
        };
        if (updated != current)
        {
            records[index] = updated;
            await SaveAsync(records, cancellationToken);
        }

        // The run is not executing while it waits, so the execution lease must not block a
        // later resume from another process.
        ReleaseInvocationLease(current.InvocationId);
        return updated;
    }

    public async Task MarkUncertainAsync(
        string callerId,
        string idempotencyKey,
        string error,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);
        var index = FindRecordIndex(records, callerId, idempotencyKey);
        if (index < 0)
            throw new InvalidOperationException("Meta invocation claim was not found.");

        var current = records[index];
        if (current.Status == MetaInvocationStatus.Uncertain && current.Error == error)
            return;
        if (current.Status is not (MetaInvocationStatus.Running or MetaInvocationStatus.AwaitingInput))
            throw new InvalidOperationException("Only a running or paused meta invocation can be marked uncertain.");

        records[index] = current with
        {
            Status = MetaInvocationStatus.Uncertain,
            Error = error,
            Pause = null
        };
        await SaveAsync(records, cancellationToken);
        ReleaseInvocationLease(current.InvocationId);
    }

    /// <summary>
    /// Claims the right to resume a paused invocation. Returns null when the record is no
    /// longer paused or another resume is already in flight, so a caller can never re-enter
    /// the same run twice.
    /// </summary>
    public async Task<MetaInvocationRecord?> TryBeginResumeAsync(
        string callerId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);
        var index = FindRecordIndex(records, callerId, idempotencyKey);
        if (index < 0)
            return null;

        var current = records[index];
        if (current.Status != MetaInvocationStatus.AwaitingInput || _activeLeases.ContainsKey(current.InvocationId))
            return null;

        var lease = TryAcquireInvocationLease(current.InvocationId);
        if (lease is null)
            return null;
        if (!_activeLeases.TryAdd(current.InvocationId, lease))
        {
            lease.Dispose();
            return null;
        }

        return current;
    }

    /// <summary>Reads one ledger record without creating or mutating anything.</summary>
    public async Task<MetaInvocationRecord?> TryGetAsync(
        string callerId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);
        var index = FindRecordIndex(records, callerId, idempotencyKey);
        return index < 0 ? null : records[index];
    }

    public async Task MarkInterruptedInvocationsUncertainAsync(CancellationToken cancellationToken)
    {
        using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);
        var changed = false;
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record.Status != MetaInvocationStatus.Running || _activeLeases.ContainsKey(record.InvocationId))
                continue;

            var lease = TryAcquireInvocationLease(record.InvocationId);
            if (lease is null)
                continue;

            using (lease)
            {
                records[index] = record with
                {
                    Status = MetaInvocationStatus.Uncertain,
                    Error = "Invocation was interrupted before a terminal result was persisted."
                };
                changed = true;
            }
        }

        if (changed)
            await SaveAsync(records, cancellationToken);
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsLockContention(ex))
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private FileStream? TryAcquireInvocationLease(Guid invocationId)
    {
        var leasePath = Path.Combine(Path.GetDirectoryName(_path)!, $"meta-invocation-{invocationId:N}.lease");
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            return new FileStream(leasePath, options);
        }
        catch (IOException ex) when (IsLockContention(ex))
        {
            return null;
        }
    }

    private static bool IsLockContention(IOException exception)
        => (exception.HResult & 0xffff) is 11 or 32 or 33 or 35;

    private void ReleaseInvocationLease(Guid invocationId)
    {
        if (_activeLeases.TryRemove(invocationId, out var lease))
            lease.Dispose();
    }

    private async Task<List<MetaInvocationRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return [];

        var json = await File.ReadAllTextAsync(_path, cancellationToken);
        var records = JsonSerializer.Deserialize(json, MetaInvocationJsonContext.Default.ListMetaInvocationRecord)
            ?? throw new InvalidDataException("Invalid meta invocation ledger.");
        if (records.Any(record => record is null ||
            string.IsNullOrWhiteSpace(record.CallerId) ||
            string.IsNullOrWhiteSpace(record.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(record.RequestHash) ||
            record.InvocationId == Guid.Empty ||
            !Enum.IsDefined(record.Status) ||
            (record.Status == MetaInvocationStatus.Completed && record.Result is null && record.Error is null) ||
            (record.Status == MetaInvocationStatus.AwaitingInput && record.Pause is null) ||
            (record.Status != MetaInvocationStatus.AwaitingInput && record.Pause is not null)) ||
            records.Select(record => (record.CallerId, record.IdempotencyKey)).Distinct().Count() != records.Count)
        {
            throw new InvalidDataException("Invalid meta invocation ledger records.");
        }

        return records;
    }

    private async Task SaveAsync(List<MetaInvocationRecord> records, CancellationToken cancellationToken)
    {
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    records,
                    MetaInvocationJsonContext.Default.ListMetaInvocationRecord,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static int FindRecordIndex(List<MetaInvocationRecord> records, string callerId, string idempotencyKey)
        => records.FindIndex(record =>
            string.Equals(record.CallerId, callerId, StringComparison.Ordinal) &&
            string.Equals(record.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));

    public void Dispose()
    {
        foreach (var invocationId in _activeLeases.Keys)
            ReleaseInvocationLease(invocationId);
    }
}