namespace OpenClaw.Core.Models;

/// <summary>
/// The closed set of inbox item kinds (design §10.1).
/// </summary>
/// <remarks>
/// <para>
/// Kind decides two things and nothing else: the title template an operator reads, and who is
/// allowed to close the item. Everything about severity, correlation and TTL hangs off that.
/// </para>
/// <para>
/// All kinds but <see cref="Notification"/> are <em>resource driven</em>: their close is owned by
/// the thing they describe (a run ends, a server recovers, an index rebuilds). A human can
/// acknowledge one, but "resolve" is refused with <see cref="InboxErrorCodes.ResourceDriven"/> -
/// confirming a todo item is not the same as fixing the resource behind it.
/// </para>
/// </remarks>
public static class InboxKinds
{
    /// <summary>A tool step is blocked on a human decision.</summary>
    public const string Approval = "approval";

    /// <summary>A run parked in <c>AwaitingInput</c>.</summary>
    public const string RunPaused = "run_paused";

    /// <summary>A run ended in a failed or uncertain terminal state.</summary>
    public const string RunFailed = "run_failed";

    /// <summary>An MCP server is down.</summary>
    public const string McpDown = "mcp_down";

    /// <summary>The skill index is degraded (some skills failed to load).</summary>
    public const string SkillDegraded = "skill_degraded";

    /// <summary>
    /// An externally ingested notice (the DrasiWake <c>inbox</c> notification channel).
    /// </summary>
    /// <remarks>
    /// Not in the Phase 3 design's five kinds, and added on purpose: without it the
    /// <c>inbox</c> notification channel has no receiver, and "notifications that must not be lost"
    /// has no landing place in this process. Unlike the other kinds nothing here owns the close, so
    /// a human resolves it.
    /// </remarks>
    public const string Notification = "notification";

    public static readonly IReadOnlyList<string> All =
    [
        Approval,
        RunPaused,
        RunFailed,
        McpDown,
        SkillDegraded,
        Notification
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    public static bool IsKnown(string? kind) => kind is not null && Known.Contains(kind);

    /// <summary>True when the underlying resource owns the close, so a manual resolve is a lie.</summary>
    public static bool IsResourceDriven(string kind) => kind != Notification;

    /// <summary>The default severity a kind is opened with; an ingest may raise it, never lower it.</summary>
    public static string DefaultSeverity(string kind) => kind switch
    {
        Approval => InboxSeverities.Warning,
        RunPaused => InboxSeverities.Info,
        RunFailed => InboxSeverities.Critical,
        McpDown => InboxSeverities.Critical,
        SkillDegraded => InboxSeverities.Warning,
        _ => InboxSeverities.Info
    };
}

/// <summary>Severity vocabulary, shared with the notification contract so one word means one thing.</summary>
public static class InboxSeverities
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Critical = "Critical";

    public static int Rank(string severity) => severity switch
    {
        Critical => 3,
        Warning => 2,
        _ => 1
    };

    public static bool IsKnown(string? severity) => severity is Info or Warning or Critical;

    /// <summary>Higher of two severities; an unknown value never wins, even when the other is equal.</summary>
    public static string Max(string left, string right)
    {
        if (!IsKnown(right))
            return IsKnown(left) ? left : Info;
        if (!IsKnown(left))
            return right;

        return Rank(right) > Rank(left) ? right : left;
    }
}

/// <summary>
/// Item lifecycle (design §10.1). <see cref="Open"/> and <see cref="Acknowledged"/> are the two
/// active states; the other two are terminal and never move again.
/// </summary>
public static class InboxStates
{
    public const string Open = "Open";
    public const string Acknowledged = "Acknowledged";
    public const string Resolved = "Resolved";
    public const string Expired = "Expired";

    public static readonly IReadOnlyList<string> All = [Open, Acknowledged, Resolved, Expired];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    public static bool IsKnown(string? state) => state is not null && Known.Contains(state);

    public static bool IsActive(string? state) => state is Open or Acknowledged;

    public static bool IsTerminal(string? state) => state is Resolved or Expired;

    /// <summary>Wire form used by <c>?state=</c>. Accepts the lowercase spelling an operator types.</summary>
    public static string? ParseQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        foreach (var state in All)
        {
            if (string.Equals(state, value, StringComparison.OrdinalIgnoreCase))
                return state;
        }

        // `active` is a filter, not a state: the list endpoint expands it into Open + Acknowledged.
        return string.Equals(value, "active", StringComparison.OrdinalIgnoreCase) ? "active" : null;
    }
}

/// <summary>How an item ended. A closed set, because it is what a dashboard groups by.</summary>
public static class InboxResolutionCodes
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Recovered = "recovered";
    public const string AutoClosed = "auto_closed";
    public const string Resolved = "resolved";
    public const string Expired = "expired";

    public static readonly IReadOnlyList<string> All =
    [
        Approved, Rejected, Completed, Failed, Recovered, AutoClosed, Resolved, Expired
    ];
}

/// <summary>Error codes this surface returns. Closed so a dashboard can switch on them.</summary>
public static class InboxErrorCodes
{
    public const string Disabled = "inbox.disabled";
    public const string NotFound = "inbox.not_found";
    public const string BadRequest = "inbox.bad_request";
    public const string InvalidState = "inbox.invalid_state";

    /// <summary>A human tried to resolve an item whose close belongs to the resource.</summary>
    public const string ResourceDriven = "inbox.resource_driven";

    /// <summary>Snooze target is not in the future.</summary>
    public const string SnoozeInPast = "inbox.snooze_in_past";
}

/// <summary>
/// What an item is about. Every field is an identifier, never content: an operator clicks through
/// to the run or the server, and the notification path stays free of prompt text and model output.
/// </summary>
public sealed record InboxSubject
{
    public Guid? InvocationId { get; init; }
    public string? ApprovalId { get; init; }
    public string? SessionId { get; init; }
    public string? ServerId { get; init; }
    public string? StepId { get; init; }

    /// <summary>Stable id of the ingested notification this item came from (kind <c>notification</c>).</summary>
    public string? NotificationId { get; init; }

    /// <summary>
    /// Identity used for de-duplication and for the item id: two active items that describe the same
    /// resource are one item, however many times the event fires.
    /// </summary>
    public string ResourceKey { get; init; } = string.Empty;
}

/// <summary>
/// One durable todo (design §10.1). Everything here is safe to show a human and safe to log:
/// a fixed title template, identifiers, and closed-set labels.
/// </summary>
public sealed record InboxItem
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = InboxKinds.Notification;
    public string Severity { get; init; } = InboxSeverities.Info;

    /// <summary>Fixed template for the kind. Never interpolates user data.</summary>
    public string Title { get; init; } = string.Empty;

    public InboxSubject Subject { get; init; } = new();
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public string State { get; init; } = InboxStates.Open;

    /// <summary>Set while snoozed; the sweep flips the item back to <c>Open</c> when it passes.</summary>
    public DateTimeOffset? SnoozeUntilUtc { get; init; }

    public DateTimeOffset? ResolvedAtUtc { get; init; }
    public string? ResolutionCode { get; init; }

    /// <summary>Absolute deadline, resolved from the kind's TTL when the item was opened.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public bool IsActive => InboxStates.IsActive(State);
}

/// <summary><c>GET /api/inbox</c> payload: keyset page over <c>(CreatedAtUtc, Id)</c>.</summary>
public sealed record InboxListResponse(
    IReadOnlyList<InboxItem> Items,
    string? NextCursor,
    InboxCounts Counts);

/// <summary>Active totals, so a dashboard badge does not have to page the whole list.</summary>
public sealed record InboxCounts(int Open, int Acknowledged, int Total);

/// <summary>One change frame on <c>GET /api/inbox/stream</c>.</summary>
public sealed record InboxStreamEvent(long Seq, string Change, InboxItem Item)
{
    public const string Opened = "opened";
    public const string Updated = "updated";
    public const string Resolved = "resolved";
    public const string Expired = "expired";
}

/// <summary><c>POST /api/inbox/{id}/snooze</c> body.</summary>
public sealed record InboxSnoozeRequest(DateTimeOffset UntilUtc);

/// <summary><c>POST /api/inbox/{id}/resolve</c> body; a null body means the generic human code.</summary>
public sealed record InboxResolveRequest(string? ResolutionCode);

/// <summary>Result of a rebuild (<c>POST /api/admin/inbox/rebuild</c>).</summary>
public sealed record InboxRebuildResponse(int Items, int EventsRead, int Skipped);

/// <summary>
/// The v2 notification envelope as it arrives from DrasiWake's <c>inbox</c> channel (design §9.1).
/// </summary>
/// <remarks>
/// Declared here rather than shared with DrasiWake: the two processes only promise the JSON shape,
/// and a shared assembly would turn every field addition into a joint release.
/// </remarks>
public sealed record InboxNotificationEnvelope
{
    public int SchemaVersion { get; init; } = 2;
    public string Id { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Severity { get; init; } = InboxSeverities.Info;
    public string SubjectStableId { get; init; } = string.Empty;
    public InboxNotificationResource? Resource { get; init; }
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, string>? Attributes { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }
    public string? DedupeKey { get; init; }
}

/// <summary><c>{kind, id}</c> pair inside an ingested envelope; <c>id</c> is already truncated upstream.</summary>
public sealed record InboxNotificationResource
{
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
}

/// <summary>
/// Inbox settings (<c>OpenClaw:Inbox</c>).
/// </summary>
/// <remarks>
/// TTL is per kind because the kinds age differently: a paused run is worth chasing for a week, a
/// down MCP server stops being useful after a day (the probe will have re-fired or a human fixed it).
/// </remarks>
public sealed class InboxConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>How often expired items and elapsed snoozes are applied.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-kind TTL overrides; anything missing falls back to <see cref="DefaultTtl"/>.</summary>
    public Dictionary<string, TimeSpan> Ttl { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Upper bound on one list page, so a client cannot ask for the whole ledger.</summary>
    public int MaxPageSize { get; set; } = 200;

    public static TimeSpan DefaultTtl(string kind) => kind switch
    {
        InboxKinds.McpDown => TimeSpan.FromDays(1),
        InboxKinds.RunFailed => TimeSpan.FromDays(7),
        InboxKinds.SkillDegraded => TimeSpan.FromDays(1),
        InboxKinds.Approval => TimeSpan.FromDays(7),
        InboxKinds.RunPaused => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(7)
    };

    /// <summary>Configured TTL for <paramref name="kind"/>, or the built-in default.</summary>
    public TimeSpan TtlFor(string kind)
        => Ttl.TryGetValue(kind, out var configured) && configured > TimeSpan.Zero
            ? configured
            : DefaultTtl(kind);

    /// <summary>
    /// Copy with every value clamped to something the projection can use, mirroring
    /// <c>McpHealthConfig.Normalize</c>: a zero sweep interval would spin, and a zero page size
    /// would make the list answer empty pages forever.
    /// </summary>
    public InboxConfig Normalize()
    {
        var ttl = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var (kind, value) in Ttl)
        {
            if (value > TimeSpan.Zero)
                ttl[kind] = value;
        }

        return new InboxConfig
        {
            Enabled = Enabled,
            SweepInterval = SweepInterval >= TimeSpan.FromSeconds(1)
                ? SweepInterval
                : TimeSpan.FromSeconds(30),
            Ttl = ttl,
            MaxPageSize = Math.Clamp(MaxPageSize, 1, 1000)
        };
    }
}
