using OpenClaw.Core.Inbox;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B7 lifecycle (design §10.1). These are the transitions with no IO and no clock of their own, so
/// every rule the surface depends on is asserted here first.
/// </summary>
public sealed class InboxStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 2, 15, 0, TimeSpan.Zero);

    [Fact]
    public void T_B7_6_Ack_is_idempotent_and_does_not_bump_the_update_stamp()
    {
        var item = Item();

        var first = InboxStateMachine.Acknowledge(item, Now);
        Assert.True(first.Changed);
        Assert.Equal(InboxStates.Acknowledged, first.Item.State);

        var second = InboxStateMachine.Acknowledge(first.Item, Now.AddMinutes(5));
        Assert.False(second.Changed);
        Assert.Equal(Now, second.Item.UpdatedAtUtc);
    }

    [Fact]
    public void Ack_on_a_closed_item_is_refused_rather_than_reopened()
    {
        var resolved = Item(state: InboxStates.Resolved);

        var mutation = InboxStateMachine.Acknowledge(resolved, Now);

        Assert.False(mutation.Changed);
        Assert.Equal(InboxErrorCodes.InvalidState, mutation.ErrorCode);
    }

    [Fact]
    public void T_B7_4_Snooze_returns_the_item_to_open_and_wakes_exactly_once()
    {
        var acknowledged = Item(state: InboxStates.Acknowledged);

        var snoozed = InboxStateMachine.Snooze(acknowledged, Now.AddHours(2), Now);
        Assert.True(snoozed.Changed);
        Assert.Equal(InboxStates.Open, snoozed.Item.State);
        Assert.Equal(Now.AddHours(2), snoozed.Item.SnoozeUntilUtc);

        // Not yet due: nothing happens, however often the sweep runs.
        Assert.False(InboxStateMachine.Wake(snoozed.Item, Now.AddHours(1)).Changed);

        var woke = InboxStateMachine.Wake(snoozed.Item, Now.AddHours(2));
        Assert.True(woke.Changed);
        Assert.Null(woke.Item.SnoozeUntilUtc);

        // The reminder fires once: the second sweep has nothing left to clear.
        Assert.False(InboxStateMachine.Wake(woke.Item, Now.AddHours(3)).Changed);
    }

    [Fact]
    public void A_snooze_target_in_the_past_is_a_bad_request_not_a_silent_no_op()
    {
        var mutation = InboxStateMachine.Snooze(Item(), Now.AddMinutes(-1), Now);

        Assert.False(mutation.Changed);
        Assert.Equal(InboxErrorCodes.SnoozeInPast, mutation.ErrorCode);
    }

    [Fact]
    public void T_B7_3_A_resource_change_closes_an_acknowledged_item_directly()
    {
        var acknowledged = InboxStateMachine.Acknowledge(Item(), Now).Item;

        var resolved = InboxStateMachine.Resolve(acknowledged, InboxResolutionCodes.Approved, Now.AddMinutes(1));

        Assert.True(resolved.Changed);
        Assert.Equal(InboxStates.Resolved, resolved.Item.State);
        Assert.Equal(InboxResolutionCodes.Approved, resolved.Item.ResolutionCode);
        Assert.Equal(Now.AddMinutes(1), resolved.Item.ResolvedAtUtc);
    }

    [Fact]
    public void T_B7_6_A_second_resolve_keeps_the_first_resolution_timestamp()
    {
        var resolved = InboxStateMachine.Resolve(Item(), InboxResolutionCodes.Approved, Now).Item;

        var again = InboxStateMachine.Resolve(resolved, InboxResolutionCodes.Rejected, Now.AddHours(1));

        Assert.False(again.Changed);
        Assert.Equal(InboxResolutionCodes.Approved, again.Item.ResolutionCode);
        Assert.Equal(Now, again.Item.ResolvedAtUtc);
    }

    [Fact]
    public void A_resolve_on_an_expired_item_is_refused()
    {
        var expired = Item(state: InboxStates.Expired);

        var mutation = InboxStateMachine.Resolve(expired, InboxResolutionCodes.Resolved, Now);

        Assert.False(mutation.Changed);
        Assert.Equal(InboxErrorCodes.InvalidState, mutation.ErrorCode);
    }

    [Fact]
    public void T_B7_10_Expiry_is_not_resolution()
    {
        var item = Item(expiresAt: Now.AddDays(7));

        Assert.False(InboxStateMachine.Expire(item, Now.AddDays(6)).Changed);

        var expired = InboxStateMachine.Expire(item, Now.AddDays(7));
        Assert.True(expired.Changed);
        Assert.Equal(InboxStates.Expired, expired.Item.State);
        Assert.Equal(InboxResolutionCodes.Expired, expired.Item.ResolutionCode);

        // A dashboard that grouped these together would report forgotten problems as fixed ones.
        Assert.Null(expired.Item.ResolvedAtUtc);
    }

    [Fact]
    public void Expiry_leaves_a_terminal_item_alone()
    {
        var resolved = Item(state: InboxStates.Resolved, expiresAt: Now.AddDays(-1));

        Assert.False(InboxStateMachine.Expire(resolved, Now).Changed);
    }

    [Fact]
    public void The_item_id_is_derived_from_kind_resource_and_opening_sequence()
    {
        var first = InboxStateMachine.ComputeId(InboxKinds.RunPaused, "abc", 3);
        var same = InboxStateMachine.ComputeId(InboxKinds.RunPaused, "abc", 3);
        var otherKind = InboxStateMachine.ComputeId(InboxKinds.Approval, "abc", 3);
        var otherSeq = InboxStateMachine.ComputeId(InboxKinds.RunPaused, "abc", 4);

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherKind);
        Assert.NotEqual(first, otherSeq);
        Assert.StartsWith("ib_", first, StringComparison.Ordinal);
        Assert.Equal(27, first.Length);
    }

    [Fact]
    public void Only_the_notification_kind_can_be_closed_by_a_person()
    {
        Assert.False(InboxKinds.IsResourceDriven(InboxKinds.Notification));

        foreach (var kind in InboxKinds.All.Where(kind => kind != InboxKinds.Notification))
            Assert.True(InboxKinds.IsResourceDriven(kind), kind);
    }

    [Fact]
    public void Titles_are_fixed_templates_per_kind()
    {
        foreach (var kind in InboxKinds.All)
        {
            var title = InboxStateMachine.TitleFor(kind);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.DoesNotContain("{", title, StringComparison.Ordinal);
        }

        Assert.Equal(InboxStateMachine.TitleFor(InboxKinds.McpDown), InboxStateMachine.TitleFor(InboxKinds.McpDown));
    }

    [Fact]
    public void Severity_ranking_never_lets_an_unknown_value_win()
    {
        Assert.Equal(InboxSeverities.Warning, InboxSeverities.Max(InboxSeverities.Info, InboxSeverities.Warning));
        Assert.Equal(InboxSeverities.Critical, InboxSeverities.Max(InboxSeverities.Critical, "Bogus"));
        Assert.Equal(InboxSeverities.Info, InboxSeverities.Max("Bogus", InboxSeverities.Info));
    }

    private static InboxItem Item(
        string kind = InboxKinds.Approval,
        string state = InboxStates.Open,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? snoozeUntil = null)
        => new()
        {
            Id = "ib_0123456789abcdef01234567",
            Kind = kind,
            Severity = InboxKinds.DefaultSeverity(kind),
            Title = InboxStateMachine.TitleFor(kind),
            Subject = new InboxSubject { ResourceKey = "resource" },
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            State = state,
            ExpiresAtUtc = expiresAt,
            SnoozeUntilUtc = snoozeUntil
        };
}
