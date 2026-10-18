using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Inbox;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Inbox;

/// <summary>Everything the projector needs to open an item, minus the decisions the store owns.</summary>
public sealed record InboxOpenRequest
{
    public string Kind { get; init; } = InboxKinds.Notification;

    /// <summary>Requested severity; a kind's default applies when this is null or unknown.</summary>
    public string? Severity { get; init; }

    public InboxSubject Subject { get; init; } = new();

    /// <summary>The sequence (or any monotonic counter) that makes the id reproduce on rebuild.</summary>
    public long OpenedSeq { get; init; }

    /// <summary>Creation time; a rebuild passes the journal's timestamp, live code passes now.</summary>
    public DateTimeOffset? CreatedAtUtc { get; init; }
}

/// <summary>
/// Durable inbox items: an append-only NDJSON journal at <c>{storage}/inbox/items.ndjson</c>, with
/// the current version of every item in memory.
/// </summary>
/// <remarks>
/// <para>
/// Append-only rather than one file per item, because the interesting record here is the
/// <em>transition</em>: an item that was acknowledged and later resolved by the resource is a small
/// history, and rewriting a whole file per transition would either lose it or cost a rename storm.
/// The journal is compacted to one line per item whenever it is loaded (see <see cref="LoadAsync"/>)
/// and on every rebuild.
/// </para>
/// <para>
/// Reads are tolerant in the same way the run journal is: a truncated last line after a crash is
/// skipped and counted, because a broken todo list must still show the items that survived.
/// </para>
/// </remarks>
public sealed class InboxStore
{
    public const string BucketName = "inbox";
    public const string JournalFileName = "items.ndjson";

    private readonly InboxConfig config;
    private readonly InboxStream stream;
    private readonly TimeProvider time;
    private readonly ILogger<InboxStore>? logger;
    private readonly string journalPath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, InboxItem> items = new(StringComparer.Ordinal);

    private int skippedLines;

    public InboxStore(
        InboxConfig config,
        string storagePath,
        InboxStream stream,
        TimeProvider? time = null,
        ILogger<InboxStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        ArgumentNullException.ThrowIfNull(stream);

        this.config = config;
        this.stream = stream;
        this.time = time ?? TimeProvider.System;
        this.logger = logger;

        var directory = Path.GetFullPath(Path.Combine(storagePath, BucketName));
        Directory.CreateDirectory(directory);
        journalPath = Path.Combine(directory, JournalFileName);
    }

    public string JournalPath => journalPath;

    /// <summary>Lines discarded because they could not be parsed; non-zero means a damaged journal.</summary>
    public int SkippedLines => Volatile.Read(ref skippedLines);

    /// <summary>
    /// Loads the journal, keeps the last version of every item, and compacts when the file has grown
    /// past a few versions per item.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            items.Clear();
            if (!File.Exists(journalPath))
                return;

            var lines = await File.ReadAllLinesAsync(journalPath, cancellationToken).ConfigureAwait(false);
            var parsed = 0;
            foreach (var line in lines)
            {
                if (line.Length == 0)
                    continue;

                InboxItem? item;
                try
                {
                    item = JsonSerializer.Deserialize(line, CoreJsonContext.Default.InboxItem);
                }
                catch (JsonException)
                {
                    item = null;
                }

                if (item is null || string.IsNullOrEmpty(item.Id))
                {
                    Interlocked.Increment(ref skippedLines);
                    continue;
                }

                items[item.Id] = item;
                parsed++;
            }

            if (parsed > items.Count)
            {
                logger?.LogInformation(
                    "Compacting inbox journal: {Lines} line(s) for {Items} item(s).", parsed, items.Count);
                await RewriteAsync([.. items.Values], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<InboxItem?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return items.GetValueOrDefault(id);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Opens an item, or bumps the severity of the one that already describes the same resource.
    /// </summary>
    /// <remarks>
    /// De-duplication is by <c>(kind, resourceKey)</c> over <em>active</em> items only: the same step
    /// pausing twice is one todo, but a run that fails after a previous failure was archived is a
    /// new todo. Without this, every probe round of a down server would add another row.
    /// </remarks>
    public async Task<(InboxItem Item, bool Created)> OpenAsync(
        InboxOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!InboxKinds.IsKnown(request.Kind))
            throw new ArgumentOutOfRangeException(nameof(request), $"Unknown inbox kind '{request.Kind}'.");
        if (string.IsNullOrEmpty(request.Subject.ResourceKey))
            throw new ArgumentOutOfRangeException(nameof(request), "InboxSubject.ResourceKey is required.");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var severity = InboxSeverities.IsKnown(request.Severity)
                ? request.Severity!
                : InboxKinds.DefaultSeverity(request.Kind);

            if (FindActive(request.Kind, request.Subject.ResourceKey) is { } existing)
            {
                var raised = InboxSeverities.Max(existing.Severity, severity);
                if (raised == existing.Severity)
                    return (existing, false);

                var bumped = existing with { Severity = raised, UpdatedAtUtc = time.GetUtcNow() };
                await PersistAsync(bumped, cancellationToken).ConfigureAwait(false);
                return (bumped, false);
            }

            var now = time.GetUtcNow();
            var createdAt = request.CreatedAtUtc ?? now;
            var item = new InboxItem
            {
                Id = InboxStateMachine.ComputeId(request.Kind, request.Subject.ResourceKey, request.OpenedSeq),
                Kind = request.Kind,
                Severity = severity,
                Title = InboxStateMachine.TitleFor(request.Kind),
                Subject = request.Subject,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = createdAt,
                State = InboxStates.Open,
                ExpiresAtUtc = createdAt + config.TtlFor(request.Kind)
            };

            if (items.TryGetValue(item.Id, out var collision))
            {
                if (collision.IsActive)
                    return (collision, false);

                // Same id, previous episode already closed: the resource came back to the same state
                // (a server that went down twice, an index that degraded twice). Re-open the same
                // todo instead of minting a new id, so a rebuild lands on the same identity.
                var reopened = item with
                {
                    Severity = InboxSeverities.Max(collision.Severity, severity),
                    State = InboxStates.Open
                };

                await PersistAsync(reopened, cancellationToken).ConfigureAwait(false);
                return (reopened, true);
            }

            await PersistAsync(item, cancellationToken).ConfigureAwait(false);
            return (item, true);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<InboxMutation> AcknowledgeAsync(string id, CancellationToken cancellationToken = default)
        => MutateAsync(id, item => InboxStateMachine.Acknowledge(item, time.GetUtcNow()), cancellationToken);

    public Task<InboxMutation> SnoozeAsync(
        string id,
        DateTimeOffset untilUtc,
        CancellationToken cancellationToken = default)
        => MutateAsync(id, item => InboxStateMachine.Snooze(item, untilUtc, time.GetUtcNow()), cancellationToken);

    /// <summary>
    /// Human resolve. Resource-driven kinds are refused here rather than in the handler, so every
    /// caller - current or future - gets the same answer (T-B7-7).
    /// </summary>
    public Task<InboxMutation> ResolveAsync(
        string id,
        string resolutionCode,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            id,
            item => InboxKinds.IsResourceDriven(item.Kind)
                ? InboxMutation.Rejected(item, InboxErrorCodes.ResourceDriven)
                : InboxStateMachine.Resolve(item, resolutionCode, time.GetUtcNow()),
            cancellationToken);

    /// <summary>Closes the active item for a resource, if one exists. Used by the projector.</summary>
    public async Task<bool> ResolveByResourceAsync(
        string kind,
        string resourceKey,
        string resolutionCode,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (FindActive(kind, resourceKey) is not { } existing)
                return false;

            var mutation = InboxStateMachine.Resolve(existing, resolutionCode, time.GetUtcNow());
            if (!mutation.Changed)
                return false;

            await PersistAsync(mutation.Item, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Applies elapsed snoozes and deadlines. Returns how many items changed.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            var changed = 0;
            foreach (var item in items.Values.Where(item => item.IsActive).ToList())
            {
                var expired = InboxStateMachine.Expire(item, now);
                var mutation = expired.Changed ? expired : InboxStateMachine.Wake(item, now);
                if (!mutation.Changed)
                    continue;

                await PersistAsync(mutation.Item, cancellationToken).ConfigureAwait(false);
                changed++;
            }

            return changed;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Replaces the projection with <paramref name="derived"/>, keeping whatever a human already
    /// decided about items that still exist.
    /// </summary>
    /// <remarks>
    /// A rebuild must not un-acknowledge somebody's todo: the journals record what the resource did,
    /// not what an operator did about it. Human-owned fields are therefore carried over by id, which
    /// is also what makes a rebuild on a converged system a no-op (T-B7-5).
    /// </remarks>
    public async Task<int> RebuildAsync(
        IReadOnlyList<InboxItem> derived,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(derived);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = new Dictionary<string, InboxItem>(StringComparer.Ordinal);
            foreach (var item in derived)
            {
                if (items.TryGetValue(item.Id, out var existing))
                {
                    next[item.Id] = Merge(existing, item);
                    continue;
                }

                next[item.Id] = item;
            }

            items.Clear();
            foreach (var (id, item) in next)
                items[id] = item;

            await RewriteAsync([.. items.Values], cancellationToken).ConfigureAwait(false);
            logger?.LogInformation("Inbox rebuilt: {Items} item(s) derived from the journals.", items.Count);

            foreach (var item in items.Values)
                stream.Publish(InboxStreamEvent.Updated, item);

            return items.Count;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<InboxItem>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return [.. items.Values];
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Keyset page, newest first. <paramref name="state"/> accepts <c>active</c> as a shorthand for
    /// the two live states; <paramref name="cursor"/> is the opaque value from the previous page.
    /// </summary>
    public async Task<InboxListResponse> ListAsync(
        string? state,
        string? kind,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, Math.Max(1, config.MaxPageSize));
        var (cursorCreatedAt, cursorId) = ParseCursor(cursor);
        var filterState = InboxStates.ParseQuery(state);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var counts = new InboxCounts(
                items.Values.Count(item => item.State == InboxStates.Open),
                items.Values.Count(item => item.State == InboxStates.Acknowledged),
                items.Values.Count(item => item.IsActive));

            var query = items.Values.AsEnumerable();
            query = filterState switch
            {
                "active" => query.Where(item => InboxStates.IsActive(item.State)),
                { } exact => query.Where(item => item.State == exact),
                null => query
            };

            if (!string.IsNullOrWhiteSpace(kind))
                query = query.Where(item => item.Kind == kind);

            var ordered = query
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .ToList();

            if (cursorCreatedAt is { } afterCreatedAt)
            {
                ordered = ordered
                    .Where(item => item.CreatedAtUtc < afterCreatedAt ||
                        (item.CreatedAtUtc == afterCreatedAt &&
                         string.CompareOrdinal(item.Id, cursorId) < 0))
                    .ToList();
            }

            var page = ordered.Take(pageSize + 1).ToList();
            string? next = null;
            if (page.Count > pageSize)
            {
                page.RemoveAt(page.Count - 1);
                next = BuildCursor(page[^1]);
            }

            return new InboxListResponse(page, next, counts);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Decides what survives a rebuild for one id.</summary>
    /// <remarks>
    /// Three cases in order. The derived item sits at the same lifecycle position, so the stored
    /// record wins outright - its timestamps are the ones that actually happened, and this is what
    /// makes a rebuild on a converged system field-for-field equal (T-B7-5). Both are active, so the
    /// human's acknowledgement and snooze are carried over. Otherwise the derived item wins: the
    /// journals are the newer truth, which is the case where an item re-opened after being closed.
    /// </remarks>
    private static InboxItem Merge(InboxItem existing, InboxItem derived)
    {
        if (existing.State == derived.State &&
            string.Equals(existing.ResolutionCode, derived.ResolutionCode, StringComparison.Ordinal))
        {
            return derived with
            {
                Severity = InboxSeverities.Max(existing.Severity, derived.Severity),
                CreatedAtUtc = existing.CreatedAtUtc,
                UpdatedAtUtc = existing.UpdatedAtUtc,
                SnoozeUntilUtc = existing.SnoozeUntilUtc,
                ResolvedAtUtc = existing.ResolvedAtUtc,
                ExpiresAtUtc = existing.ExpiresAtUtc
            };
        }

        if (existing.IsActive && derived.IsActive)
        {
            return derived with
            {
                State = existing.State,
                SnoozeUntilUtc = existing.SnoozeUntilUtc,
                CreatedAtUtc = existing.CreatedAtUtc,
                UpdatedAtUtc = existing.UpdatedAtUtc,
                ExpiresAtUtc = existing.ExpiresAtUtc
            };
        }

        return derived;
    }

    private InboxItem? FindActive(string kind, string resourceKey)
        => items.Values.FirstOrDefault(item =>
            item.IsActive &&
            string.Equals(item.Kind, kind, StringComparison.Ordinal) &&
            string.Equals(item.Subject.ResourceKey, resourceKey, StringComparison.Ordinal));

    private async Task<InboxMutation> MutateAsync(
        string id,
        Func<InboxItem, InboxMutation> transition,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!items.TryGetValue(id, out var existing))
                return InboxMutation.Rejected(new InboxItem { Id = id }, InboxErrorCodes.NotFound);

            var mutation = transition(existing);
            if (mutation.Changed)
                await PersistAsync(mutation.Item, cancellationToken).ConfigureAwait(false);

            return mutation;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Caller holds <see cref="gate"/>.</summary>
    private async Task PersistAsync(InboxItem item, CancellationToken cancellationToken)
    {
        items[item.Id] = item;

        var line = JsonSerializer.Serialize(item, CoreJsonContext.Default.InboxItem) + "\n";
        await File.AppendAllTextAsync(journalPath, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        var change = item.State switch
        {
            InboxStates.Resolved => InboxStreamEvent.Resolved,
            InboxStates.Expired => InboxStreamEvent.Expired,
            _ => InboxStreamEvent.Updated
        };
        stream.Publish(change, item);
    }

    /// <summary>Caller holds <see cref="gate"/>.</summary>
    private async Task RewriteAsync(IReadOnlyList<InboxItem> snapshot, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(snapshot.Count * 256);
        foreach (var item in snapshot.OrderBy(item => item.CreatedAtUtc))
            builder.Append(JsonSerializer.Serialize(item, CoreJsonContext.Default.InboxItem)).Append('\n');

        var temporary = journalPath + ".tmp";
        await File.WriteAllTextAsync(temporary, builder.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporary, journalPath, overwrite: true);
    }

    private static string BuildCursor(InboxItem item)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{item.CreatedAtUtc.ToUnixTimeMilliseconds()}:{item.Id}");

    private static (DateTimeOffset? CreatedAt, string Id) ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return (null, string.Empty);

        var separator = cursor.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 ||
            !long.TryParse(cursor[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis))
        {
            return (null, string.Empty);
        }

        return (DateTimeOffset.FromUnixTimeMilliseconds(millis), cursor[(separator + 1)..]);
    }
}
