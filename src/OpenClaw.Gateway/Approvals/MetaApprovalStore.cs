using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Approvals;

namespace OpenClaw.Gateway.Approvals;

public sealed class MetaApprovalOptions
{
    public bool Enabled { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan GrantTtl { get; init; } = TimeSpan.FromMinutes(15);
    public string? StoragePath { get; init; }

    public const string BucketName = "meta-approvals";
}

/// <summary>
/// Durable store of human approvals for meta-skill tool steps. One JSON file per approval,
/// written atomically: the deterministic approval id makes the create path idempotent and the
/// rename makes concurrent creates collapse onto the first winner.
/// </summary>
public sealed class MetaApprovalStore : IMetaApprovalStore, IHostedService, IDisposable
{
    private readonly string _directory;
    private readonly string _lockPath;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _grantTtl;
    private readonly ILogger<MetaApprovalStore>? _logger;
    private readonly CancellationTokenSource _sweepCancellation = new();

    public MetaApprovalStore(
        MetaApprovalOptions options,
        string fallbackStoragePath,
        ILogger<MetaApprovalStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Approval timeout must be positive.");
        if (options.GrantTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Approval grant TTL must be positive.");

        var root = string.IsNullOrWhiteSpace(options.StoragePath) ? fallbackStoragePath : options.StoragePath;
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _directory = Path.GetFullPath(Path.Combine(root, MetaApprovalOptions.BucketName));
        _lockPath = Path.Combine(_directory, ".lock");
        _timeout = options.Timeout;
        _grantTtl = options.GrantTtl;
        _logger = logger;
        Directory.CreateDirectory(_directory);
    }

    public string ComputeApprovalId(MetaApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var material = string.Join('|',
            request.SessionId,
            request.SkillName,
            request.StepId,
            request.ToolName,
            request.ArgsFingerprint);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return "map_" + hash[..24];
    }

    public async Task<MetaApprovalRecord> GetOrCreateAsync(
        MetaApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var approvalId = ComputeApprovalId(request);
        var existing = await TryGetAsync(approvalId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        using var handle = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        existing = await TryGetAsync(approvalId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var now = DateTimeOffset.UtcNow;
        var record = new MetaApprovalRecord(
            approvalId,
            request.SessionId,
            request.CallerId,
            request.SkillName,
            request.StepId,
            request.ToolName,
            request.ArgumentsJson,
            request.ArgsFingerprint,
            request.Summary,
            now,
            now.Add(_timeout),
            Decision: null,
            DecidedBy: null,
            DecidedAtUtc: null,
            DecisionReason: null,
            ConsumedAtUtc: null);
        await WriteAsync(record, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation(
            "Meta approval {ApprovalId} queued for session {SessionId} step {StepId} tool {Tool}.",
            approvalId,
            request.SessionId,
            request.StepId,
            request.ToolName);
        return record;
    }

    public async Task<MetaApprovalRecord?> TryGetAsync(string approvalId, CancellationToken cancellationToken)
    {
        if (!IsValidId(approvalId))
            return null;

        var path = PathFor(approvalId);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(json, MetaApprovalJsonContext.Default.MetaApprovalRecord);
        }
        catch (JsonException exception)
        {
            _logger?.LogError(exception, "Meta approval {ApprovalId} is not readable.", approvalId);
            return null;
        }
    }

    public async Task<MetaApprovalDecideStatus> TryDecideAsync(
        string approvalId,
        bool approved,
        string decidedBy,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        if (!IsValidId(approvalId))
            return MetaApprovalDecideStatus.NotFound;

        using var handle = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var record = await TryGetAsync(approvalId, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return MetaApprovalDecideStatus.NotFound;
        if (!record.IsPending)
            return MetaApprovalDecideStatus.AlreadyDecided;
        if (DateTimeOffset.UtcNow > record.ExpiresAtUtc)
            return MetaApprovalDecideStatus.NotFound;

        await WriteAsync(
            record with
            {
                Decision = approved ? MetaApprovalDecisions.Approved : MetaApprovalDecisions.Denied,
                DecidedBy = decidedBy,
                DecidedAtUtc = DateTimeOffset.UtcNow,
                DecisionReason = string.IsNullOrWhiteSpace(reason) ? null : reason
            },
            cancellationToken).ConfigureAwait(false);
        return MetaApprovalDecideStatus.Recorded;
    }

    public async Task<MetaApprovalRecord?> FindActiveGrantAsync(
        string sessionId,
        string toolName,
        string argsFingerprint,
        CancellationToken cancellationToken)
    {
        foreach (var record in await ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(record.SessionId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(record.ToolName, toolName, StringComparison.Ordinal) ||
                !string.Equals(record.ArgsFingerprint, argsFingerprint, StringComparison.Ordinal) ||
                !string.Equals(record.Decision, MetaApprovalDecisions.Approved, StringComparison.Ordinal) ||
                record.DecidedAtUtc is null)
            {
                continue;
            }

            if (DateTimeOffset.UtcNow <= record.DecidedAtUtc.Value.Add(_grantTtl))
                return record;
        }

        return null;
    }

    public async Task MarkConsumedAsync(string approvalId, CancellationToken cancellationToken)
    {
        if (!IsValidId(approvalId))
            return;

        using var handle = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var record = await TryGetAsync(approvalId, cancellationToken).ConfigureAwait(false);
        if (record is null || record.ConsumedAtUtc is not null)
            return;

        await WriteAsync(record with { ConsumedAtUtc = DateTimeOffset.UtcNow }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MetaApprovalRecord>> ListAsync(
        string callerId,
        string? sessionId,
        bool pendingOnly,
        CancellationToken cancellationToken)
        => (await ReadAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(record => string.Equals(record.CallerId, callerId, StringComparison.Ordinal))
            .Where(record => string.IsNullOrWhiteSpace(sessionId) ||
                             string.Equals(record.SessionId, sessionId, StringComparison.Ordinal))
            .Where(record => !pendingOnly || record.IsPending)
            .OrderBy(record => record.CreatedAtUtc)
            .ToArray();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = SweepAsync(_sweepCancellation.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sweepCancellation.Cancel();
        return Task.CompletedTask;
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    // Decided records are kept for audit for one more timeout window.
                    var retentionCutoff = now.Add(-(_timeout + _timeout));
                    foreach (var record in await ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var keep = record.IsPending
                            ? record.ExpiresAtUtc > now
                            : record.CreatedAtUtc > retentionCutoff;
                        if (keep)
                            continue;

                        File.Delete(PathFor(record.ApprovalId));
                        _logger?.LogInformation(
                            "Meta approval {ApprovalId} was removed ({State}).",
                            record.ApprovalId,
                            record.Decision ?? "pending");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger?.LogWarning(exception, "Meta approval sweep failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<IReadOnlyList<MetaApprovalRecord>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
            return [];

        var records = new List<MetaApprovalRecord>();
        foreach (var path in Directory.EnumerateFiles(_directory, "map_*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var record = JsonSerializer.Deserialize(json, MetaApprovalJsonContext.Default.MetaApprovalRecord);
                if (record is not null)
                    records.Add(record);
            }
            catch (JsonException exception)
            {
                _logger?.LogError(exception, "Meta approval file {Path} is not readable.", path);
            }
            catch (IOException)
            {
                // A concurrent write replaced the file: the next read sees the new content.
            }
        }

        return records;
    }

    private async Task WriteAsync(MetaApprovalRecord record, CancellationToken cancellationToken)
    {
        var path = PathFor(record.ApprovalId);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
                        record,
                        MetaApprovalJsonContext.Default.MetaApprovalRecord,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private string PathFor(string approvalId) => Path.Combine(_directory, approvalId + ".json");

    private static bool IsValidId(string approvalId)
        => !string.IsNullOrWhiteSpace(approvalId) &&
           approvalId.Length is > 3 and <= 64 &&
           approvalId.All(static c => char.IsLetterOrDigit(c) || c is '_' or '-');

    public void Dispose() => _sweepCancellation.Dispose();
}
