using System.Text.Json;
using System.Collections.Concurrent;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Inbox;
using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway;

public sealed class MetaInvocationStore : IHostedService, IDisposable
{
    private readonly string _path;
    private readonly string _lockPath;
    private readonly int _retentionDays;
    private readonly MetaRunEventLog _events;
    private readonly IInboxRunEventSink? _inboxEvents;
    private readonly ConcurrentDictionary<Guid, FileStream> _activeLeases = new();

    public MetaInvocationStore(
        string storagePath,
        int retentionDays,
        IInboxRunEventSink? inboxEvents = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        if (retentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention must be at least one day.");

        _path = Path.GetFullPath(Path.Combine(storagePath, "meta-invocations.json"));
        _lockPath = _path + ".lock";
        _retentionDays = retentionDays;
        _events = new MetaRunEventLog(Path.GetDirectoryName(_path)!);
        _inboxEvents = inboxEvents;
    }

    /// <summary>The run journal, exposed so read endpoints do not have to re-derive its paths.</summary>
    public MetaRunEventLog Events => _events;

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
        CancellationToken cancellationToken,
        string? sessionId = null,
        string? skill = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays);
        var expired = records.Where(candidate =>
            candidate.Status != MetaInvocationStatus.Running && candidate.CreatedAtUtc <= cutoff).ToList();
        var removed = expired.Count > 0;
        if (removed)
        {
            records.RemoveAll(candidate =>
                candidate.Status != MetaInvocationStatus.Running && candidate.CreatedAtUtc <= cutoff);
            // The journal lives and dies with its ledger row (T-B3-9): an orphaned .events.ndjson
            // would be invisible to every endpoint and grow without a bound.
            foreach (var expiredRecord in expired)
                _events.Delete(expiredRecord.InvocationId);
        }

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
            DateTimeOffset.UtcNow,
            Pause: null,
            SessionId: sessionId,
            Skill: skill);
        var lease = TryAcquireInvocationLease(record.InvocationId)
            ?? throw new IOException("Unable to acquire the meta invocation lease.");
        if (!_activeLeases.TryAdd(record.InvocationId, lease))
        {
            lease.Dispose();
            throw new InvalidOperationException("Meta invocation lease is already active in this process.");
        }

        try
        {
            record = await AppendEventsAsync(
                record,
                [new MetaRunEventDraft(MetaRunEventKinds.Claim, Status: nameof(MetaInvocationStatus.Running))],
                cancellationToken);
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
        CancellationToken cancellationToken,
        IReadOnlyList<MetaRunEventDraft>? stepEvents = null)
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

        var updated = current with
        {
            Status = MetaInvocationStatus.Completed,
            Result = result,
            Error = null,
            Pause = null
        };
        updated = await AppendEventsAsync(
            updated,
            WithTerminalEvent(stepEvents, MetaRunEventKinds.Completed, current, MetaInvocationStatus.Completed),
            cancellationToken);

        records[index] = updated;
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
        CancellationToken cancellationToken,
        IReadOnlyList<MetaRunEventDraft>? stepEvents = null)
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

        var updated = current with
        {
            Status = MetaInvocationStatus.Completed,
            Result = null,
            Error = errorCode,
            Pause = null
        };
        // A denied approval is a business failure, not an "uncertain" run: the knot of the two is
        // why the journal has separate `failed` and `uncertain` kinds over the same ledger status.
        updated = await AppendEventsAsync(
            updated,
            WithTerminalEvent(stepEvents, MetaRunEventKinds.Failed, current, MetaInvocationStatus.Completed, errorCode),
            cancellationToken);

        records[index] = updated;
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
        CancellationToken cancellationToken,
        IReadOnlyList<MetaRunEventDraft>? stepEvents = null)
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
            updated = await AppendEventsAsync(
                updated,
                WithPauseEvents(stepEvents, pause),
                cancellationToken);
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
        CancellationToken cancellationToken,
        IReadOnlyList<MetaRunEventDraft>? stepEvents = null)
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

        var updated = current with
        {
            Status = MetaInvocationStatus.Uncertain,
            Error = error,
            Pause = null
        };
        updated = await AppendEventsAsync(
            updated,
            WithTerminalEvent(stepEvents, MetaRunEventKinds.Uncertain, current, MetaInvocationStatus.Uncertain, error),
            cancellationToken);

        records[index] = updated;
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

    /// <summary>
    /// Reads one record by its invocation id, which is what the read endpoints and the dashboard
    /// have in hand (they never see the caller/idempotency pair).
    /// </summary>
    public async Task<MetaInvocationRecord?> TryGetByInvocationIdAsync(
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        await using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);
        var index = FindIndexByInvocationId(records, invocationId);
        return index < 0 ? null : records[index];
    }

    /// <summary>
    /// Appends events for an invocation the caller already knows about (a resume request, an
    /// approval decision). Takes the ledger lock so the journal stays ordered with state changes.
    /// </summary>
    public async Task AppendEventAsync(
        Guid invocationId,
        IReadOnlyList<MetaRunEventDraft> drafts,
        CancellationToken cancellationToken)
    {
        if (drafts.Count == 0)
            return;

        using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);
        var index = FindIndexByInvocationId(records, invocationId);
        if (index < 0)
            return;

        var updated = await AppendEventsAsync(records[index], drafts, cancellationToken);
        if (updated != records[index])
        {
            records[index] = updated;
            await SaveAsync(records, cancellationToken);
        }
    }

    /// <summary>
    /// Keyset page over the ledger, newest first, ordered by (CreatedAtUtc, InvocationId) so a page
    /// boundary is stable even when two rows share a timestamp.
    /// </summary>
    public async Task<(IReadOnlyList<MetaInvocationRecord> Items, bool HasMore)> ListAsync(
        string? callerId,
        string? sessionId,
        MetaInvocationStatus? status,
        int limit,
        DateTimeOffset? cursorCreatedAtUtc,
        Guid? cursorInvocationId,
        CancellationToken cancellationToken)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be at least one.");

        await using var handle = await AcquireLockAsync(cancellationToken);
        var records = await LoadAsync(cancellationToken);

        // One extra row tells the caller whether a next page exists without a second count query.
        var items = records
            .Where(record => callerId is null || string.Equals(record.CallerId, callerId, StringComparison.Ordinal))
            .Where(record => string.IsNullOrWhiteSpace(sessionId) ||
                string.Equals(record.SessionId, sessionId, StringComparison.Ordinal))
            .Where(record => status is null || record.Status == status)
            .Where(record => cursorCreatedAtUtc is null || IsBeforeCursor(record, cursorCreatedAtUtc.Value, cursorInvocationId))
            .OrderByDescending(record => record.CreatedAtUtc)
            .ThenByDescending(record => record.InvocationId)
            .Take(limit + 1)
            .ToList();

        var hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);

        return (items, hasMore);
    }

    private static bool IsBeforeCursor(
        MetaInvocationRecord record,
        DateTimeOffset cursorCreatedAtUtc,
        Guid? cursorInvocationId)
    {
        if (record.CreatedAtUtc < cursorCreatedAtUtc)
            return true;
        if (record.CreatedAtUtc > cursorCreatedAtUtc)
            return false;

        // Same instant: fall back to the id so the page cannot loop or skip a row.
        return cursorInvocationId is null || record.InvocationId.CompareTo(cursorInvocationId.Value) < 0;
    }

    private static int FindIndexByInvocationId(List<MetaInvocationRecord> records, Guid invocationId)
        => records.FindIndex(record => record.InvocationId == invocationId);

    /// <summary>
    /// Appends the given drafts and reports the resulting record. A journal write failure never
    /// fails the state transition: the run is real, so the honest outcome is EventsGap=true on an
    /// advanced ledger, not a rolled-back run that the caller would retry into a duplicate.
    /// </summary>
    private async Task<MetaInvocationRecord> AppendEventsAsync(
        MetaInvocationRecord record,
        IReadOnlyList<MetaRunEventDraft> drafts,
        CancellationToken cancellationToken)
    {
        if (drafts.Count == 0)
            return record;

        try
        {
            var kept = DedupeSynthetic(record.InvocationId, drafts);
            if (kept.Count > 0)
            {
                var atUtc = DateTimeOffset.UtcNow;
                var lastSeq = await _events.AppendAsync(record.InvocationId, kept, atUtc, cancellationToken);
                PublishToInbox(record.InvocationId, kept, lastSeq - kept.Count + 1, atUtc);
            }

            return record;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return record with { EventsGap = true };
        }
    }

    /// <summary>
    /// Hands the events that were just written to the inbox projector.
    /// </summary>
    /// <remarks>
    /// The sequence numbers are derived (<c>lastSeq - count + 1</c>) instead of read back: re-reading
    /// the journal inside the ledger lock would double the IO of every write for information this
    /// call already has. Publishing is a channel write, so it cannot fail or block; the try/catch is
    /// here because a projection is never allowed to break a state transition.
    /// </remarks>
    private void PublishToInbox(Guid invocationId, IReadOnlyList<MetaRunEventDraft> drafts, long firstSeq, DateTimeOffset atUtc)
    {
        if (_inboxEvents is null)
            return;

        try
        {
            for (var index = 0; index < drafts.Count; index++)
            {
                var draft = drafts[index];
                _inboxEvents.Publish(new MetaRunEvent(
                    invocationId,
                    firstSeq + index,
                    atUtc,
                    draft.Kind,
                    draft.StepId,
                    draft.Status,
                    draft.Detail,
                    draft.Synthetic));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // A closed queue (shutdown) is not an error worth surfacing here.
        }
    }

    /// <summary>
    /// Drops synthetic events whose (kind, step) pair is already in the journal. Derived step
    /// events describe a state snapshot, so re-deriving the same step on a later observation must
    /// not append it twice; real events (pause, resume, terminal) are never deduped because the
    /// same step legitimately pauses more than once.
    /// </summary>
    private List<MetaRunEventDraft> DedupeSynthetic(Guid invocationId, IReadOnlyList<MetaRunEventDraft> drafts)
    {
        if (!drafts.Any(static draft => draft.Synthetic))
            return [.. drafts];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in _events.Read(invocationId).Events)
        {
            if (existing.Synthetic)
                seen.Add(existing.Kind + "|" + existing.StepId);
        }

        var kept = new List<MetaRunEventDraft>(drafts.Count);
        foreach (var draft in drafts)
        {
            if (draft.Synthetic && !seen.Add(draft.Kind + "|" + draft.StepId))
                continue;
            kept.Add(draft);
        }

        return kept;
    }

    private static List<MetaRunEventDraft> WithTerminalEvent(
        IReadOnlyList<MetaRunEventDraft>? stepEvents,
        string kind,
        MetaInvocationRecord current,
        MetaInvocationStatus status,
        string? detail = null)
    {
        var drafts = new List<MetaRunEventDraft>(stepEvents ?? []);
        drafts.Add(new MetaRunEventDraft(kind, current.Pause?.StepId, status.ToString(), detail));
        return drafts;
    }

    private static List<MetaRunEventDraft> WithPauseEvents(
        IReadOnlyList<MetaRunEventDraft>? stepEvents,
        MetaInvocationPauseInfo pause)
    {
        var drafts = new List<MetaRunEventDraft>(stepEvents ?? []);
        drafts.Add(new MetaRunEventDraft(
            MetaRunEventKinds.StepPaused,
            pause.StepId,
            nameof(MetaInvocationStatus.AwaitingInput),
            pause.Reason));

        if (!string.IsNullOrWhiteSpace(pause.ApprovalId))
        {
            // The approval id itself is a machine handle; the journal carries only a stable short
            // form so a timeline never leaks a usable token.
            var shortId = pause.ApprovalId.Length <= 16 ? pause.ApprovalId : pause.ApprovalId[..16];
            drafts.Add(new MetaRunEventDraft(
                MetaRunEventKinds.ApprovalRequested,
                pause.StepId,
                nameof(MetaInvocationStatus.AwaitingInput),
                shortId));
        }

        return drafts;
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
                var interrupted = record with
                {
                    Status = MetaInvocationStatus.Uncertain,
                    Error = "Invocation was interrupted before a terminal result was persisted."
                };
                // The sweep is the only writer that turns a run terminal without an executing
                // worker, so it must leave a `uncertain` event behind or the timeline would end
                // mid-run with no explanation.
                interrupted = await AppendEventsAsync(
                    interrupted,
                    [new MetaRunEventDraft(
                        MetaRunEventKinds.Uncertain,
                        record.Pause?.StepId,
                        nameof(MetaInvocationStatus.Uncertain),
                        interrupted.Error)],
                    cancellationToken);
                records[index] = interrupted;
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