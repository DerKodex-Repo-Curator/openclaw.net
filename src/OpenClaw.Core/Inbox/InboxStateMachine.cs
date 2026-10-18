using OpenClaw.Core.Models;

namespace OpenClaw.Core.Inbox;

/// <summary>
/// Outcome of one attempted transition: the item to store, whether anything actually moved, and the
/// closed-set error code when the caller asked for something the state forbids.
/// </summary>
/// <remarks>
/// <paramref name="Changed"/> is what keeps repeated commands idempotent in the honest sense: an
/// <c>ack</c> on an already-acknowledged item returns 200 with the same item, and - crucially - does
/// not bump <c>UpdatedAtUtc</c>, so a client polling on "updated since" never sees phantom churn
/// (T-B7-6).
/// </remarks>
public readonly record struct InboxMutation(InboxItem Item, bool Changed, string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;

    public static InboxMutation Unchanged(InboxItem item) => new(item, false, null);

    public static InboxMutation ChangedTo(InboxItem item) => new(item, true, null);

    public static InboxMutation Rejected(InboxItem item, string errorCode) => new(item, false, errorCode);
}

/// <summary>
/// The item lifecycle as pure functions over <see cref="InboxItem"/> (design §10.1).
/// </summary>
/// <remarks>
/// <para>
/// Nothing here touches storage, a clock or a logger: the transitions are the part that must be
/// provably right, so they are the part with no dependencies. The store persists whatever comes
/// back, the sweep supplies <c>now</c>, and the handlers turn error codes into status codes.
/// </para>
/// <para>
/// The invariant that matters operationally: <b>a human's acknowledgement never decides anything</b>.
/// It changes the reminder cadence, nothing else. An item closed by a person while the run is still
/// blocked would be the one lie this surface can tell, so resolve-from-resource works from
/// <c>Acknowledged</c> as directly as from <c>Open</c> (T-B7-3).
/// </para>
/// </remarks>
public static class InboxStateMachine
{
    /// <summary><c>Open → Acknowledged</c>; idempotent on an already-acknowledged item.</summary>
    public static InboxMutation Acknowledge(InboxItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.State == InboxStates.Acknowledged)
            return InboxMutation.Unchanged(item);

        if (!InboxStates.IsActive(item.State))
            return InboxMutation.Rejected(item, InboxErrorCodes.InvalidState);

        return InboxMutation.ChangedTo(item with
        {
            State = InboxStates.Acknowledged,
            UpdatedAtUtc = now
        });
    }

    /// <summary>
    /// Postpones the reminder until <paramref name="untilUtc"/>. The item returns to <c>Open</c> as
    /// the design draws it - "snoozed" is a reminder state, not a lifecycle state.
    /// </summary>
    public static InboxMutation Snooze(InboxItem item, DateTimeOffset untilUtc, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!InboxStates.IsActive(item.State))
            return InboxMutation.Rejected(item, InboxErrorCodes.InvalidState);

        if (untilUtc <= now)
            return InboxMutation.Rejected(item, InboxErrorCodes.SnoozeInPast);

        return InboxMutation.ChangedTo(item with
        {
            State = InboxStates.Open,
            SnoozeUntilUtc = untilUtc,
            UpdatedAtUtc = now
        });
    }

    /// <summary>Sweep step: an elapsed snooze clears itself. Exactly one change per snooze (T-B7-4).</summary>
    public static InboxMutation Wake(InboxItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.SnoozeUntilUtc is not { } until || until > now)
            return InboxMutation.Unchanged(item);

        return InboxMutation.ChangedTo(item with
        {
            SnoozeUntilUtc = null,
            UpdatedAtUtc = now
        });
    }

    /// <summary>
    /// Closes an item because the thing it describes changed. Allowed from both active states;
    /// a second resolve is a no-op that keeps the first <paramref name="item"/>'s timestamps.
    /// </summary>
    public static InboxMutation Resolve(InboxItem item, string resolutionCode, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolutionCode);

        if (item.State == InboxStates.Resolved)
            return InboxMutation.Unchanged(item);

        if (item.State == InboxStates.Expired)
            return InboxMutation.Rejected(item, InboxErrorCodes.InvalidState);

        return InboxMutation.ChangedTo(item with
        {
            State = InboxStates.Resolved,
            ResolutionCode = resolutionCode,
            ResolvedAtUtc = now,
            SnoozeUntilUtc = null,
            UpdatedAtUtc = now
        });
    }

    /// <summary>
    /// Sweep step: past its deadline. <c>ResolvedAtUtc</c> stays null on purpose - "expired" is not
    /// "resolved", and a dashboard that counted them together would be reporting fixed problems as
    /// merely forgotten ones.
    /// </summary>
    public static InboxMutation Expire(InboxItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!InboxStates.IsActive(item.State))
            return InboxMutation.Unchanged(item);

        if (item.ExpiresAtUtc is not { } expiresAt || expiresAt > now)
            return InboxMutation.Unchanged(item);

        return InboxMutation.ChangedTo(item with
        {
            State = InboxStates.Expired,
            ResolutionCode = InboxResolutionCodes.Expired,
            SnoozeUntilUtc = null,
            UpdatedAtUtc = now
        });
    }

    /// <summary>
    /// Deterministic item id: kind, the resource it describes, and the event that opened it.
    /// </summary>
    /// <remarks>
    /// Including the opening sequence is what makes a rebuild reproduce the same ids: the journal
    /// carries the sequence, so replaying it lands on identical ids without a separate id allocator.
    /// </remarks>
    public static string ComputeId(string kind, string resourceKey, long openedSeq)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(resourceKey);

        var material = string.Join('|', kind, resourceKey, openedSeq.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();

        return "ib_" + hash[..24];
    }

    /// <summary>Fixed title per kind. A template with a variable in it would be a content leak.</summary>
    public static string TitleFor(string kind) => kind switch
    {
        InboxKinds.Approval => "A tool step is waiting for your approval",
        InboxKinds.RunPaused => "A run is paused and needs input",
        InboxKinds.RunFailed => "A run ended without success",
        InboxKinds.McpDown => "An MCP server is down",
        InboxKinds.SkillDegraded => "The skill index is degraded",
        InboxKinds.Notification => "An operational notification needs attention",
        _ => "An inbox item needs attention"
    };
}
