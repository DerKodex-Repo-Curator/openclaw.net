using OpenClaw.Core.Inbox;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Inbox;

/// <summary>
/// A dependency-free replay of the same open/resolve rules the live projector uses, so a rebuild can
/// recompute the item set from the journals without touching storage.
/// </summary>
/// <remarks>
/// <para>
/// It is a second implementation of <see cref="InboxStore"/>'s open/resolve semantics, and that is
/// the point: the store writes to disk and publishes stream frames, both of which a rebuild must not
/// do per replayed event. The two are kept honest by a test that rebuilds a converged store and
/// asserts the item set is field-for-field unchanged (T-B7-5) - drift shows up there immediately.
/// </para>
/// </remarks>
internal static class InboxReplay
{
    /// <summary>Opens (or re-opens) an item in the shadow set.</summary>
    public static void Open(
        Dictionary<string, InboxItem> items,
        InboxConfig config,
        string kind,
        string resourceKey,
        long openedSeq,
        InboxSubject subject,
        DateTimeOffset atUtc,
        string? severity = null)
    {
        var effectiveSeverity = InboxSeverities.IsKnown(severity)
            ? severity!
            : InboxKinds.DefaultSeverity(kind);

        if (FindActive(items, kind, resourceKey) is not null)
            return;

        var id = InboxStateMachine.ComputeId(kind, resourceKey, openedSeq);
        if (items.TryGetValue(id, out var existing) && existing.IsActive)
            return;

        items[id] = new InboxItem
        {
            Id = id,
            Kind = kind,
            Severity = effectiveSeverity,
            Title = InboxStateMachine.TitleFor(kind),
            Subject = subject,
            CreatedAtUtc = atUtc,
            UpdatedAtUtc = atUtc,
            State = InboxStates.Open,
            ExpiresAtUtc = atUtc + config.TtlFor(kind)
        };
    }

    /// <summary>Closes the active item for a resource, if the shadow set has one.</summary>
    public static void Resolve(
        Dictionary<string, InboxItem> items,
        string kind,
        string resourceKey,
        string resolutionCode,
        DateTimeOffset atUtc)
    {
        if (FindActive(items, kind, resourceKey) is not { } existing)
            return;

        var mutation = InboxStateMachine.Resolve(existing, resolutionCode, atUtc);
        if (mutation.Changed)
            items[existing.Id] = mutation.Item;
    }

    private static InboxItem? FindActive(Dictionary<string, InboxItem> items, string kind, string resourceKey)
        => items.Values.FirstOrDefault(item =>
            item.IsActive &&
            string.Equals(item.Kind, kind, StringComparison.Ordinal) &&
            string.Equals(item.Subject.ResourceKey, resourceKey, StringComparison.Ordinal));
}
