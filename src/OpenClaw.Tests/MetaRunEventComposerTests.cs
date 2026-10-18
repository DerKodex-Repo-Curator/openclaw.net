using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// T-B3-7: what the gateway is allowed to claim about a run it did not watch live.
/// </summary>
/// <remarks>
/// These tests exist mostly to pin down honesty, not behaviour: every event the composer produces
/// is <c>synthetic</c>, and the timestamps of a derived timeline are the run record's timestamps
/// walked forward by recorded durations — never invented "now".
/// </remarks>
public sealed class MetaRunEventComposerTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 18, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void T_B3_7_A_pause_derives_the_finished_steps_then_the_blocked_one()
    {
        var session = BuildSession(
            new StepSpec("validate", ToolResultStatuses.Completed, 120),
            new StepSpec("fetch", ToolResultStatuses.Completed, 80));

        var drafts = MetaRunEventComposer.DeriveStepsForPause(session, "triage-order", "refund");

        Assert.Equal(
            [MetaRunEventKinds.StepCompleted, MetaRunEventKinds.StepCompleted, MetaRunEventKinds.StepStarted],
            drafts.Select(draft => draft.Kind));
        Assert.Equal(["validate", "fetch", "refund"], drafts.Select(draft => draft.StepId));

        // Derived from a snapshot, not observed: every draft says so, or the timeline would
        // pretend to have watched the steps happen.
        Assert.All(drafts, draft => Assert.True(draft.Synthetic));
    }

    [Fact]
    public void T_B3_7_A_terminal_write_derives_failed_steps_with_their_failure_code()
    {
        var session = BuildSession(
            new StepSpec("validate", ToolResultStatuses.Completed, 40),
            new StepSpec("refund", ToolResultStatuses.Failed, 10, "payment_declined"));

        var drafts = MetaRunEventComposer.DeriveStepsForTerminal(session, "triage-order");

        Assert.Equal(
            [MetaRunEventKinds.StepCompleted, MetaRunEventKinds.StepFailed],
            drafts.Select(draft => draft.Kind));

        var failed = drafts[^1];
        Assert.Equal("refund", failed.StepId);
        Assert.Equal(ToolResultStatuses.Failed, failed.Status);
        Assert.Equal("payment_declined", failed.Detail);
    }

    [Fact]
    public void T_B3_7_Without_a_session_the_blocked_step_is_still_known()
    {
        // No session means no step history to derive — but the step the run is blocked on is a
        // fact from the pause record itself, so it survives.
        var pauseDrafts = MetaRunEventComposer.DeriveStepsForPause(null, "triage-order", "refund");
        var pending = Assert.Single(pauseDrafts);
        Assert.Equal(MetaRunEventKinds.StepStarted, pending.Kind);
        Assert.Equal("refund", pending.StepId);
        Assert.True(pending.Synthetic);

        Assert.Empty(MetaRunEventComposer.DeriveStepsForTerminal(null, "triage-order"));

        // A session that only ever ran a different skill is not this run's session.
        var other = BuildSessionFor("other-skill", new StepSpec("validate", ToolResultStatuses.Completed, 5));
        Assert.Empty(MetaRunEventComposer.DeriveStepsForTerminal(other, "triage-order"));
    }

    [Fact]
    public void T_B3_7_History_without_a_session_is_a_claim_and_a_terminal_event()
    {
        var record = BuildRecord(MetaInvocationStatus.Completed, error: null);

        var events = MetaRunEventComposer.FromHistory(record, session: null);

        Assert.Equal([MetaRunEventKinds.Claim, MetaRunEventKinds.Completed], events.Select(runEvent => runEvent.Kind));
        Assert.Equal([1L, 2L], events.Select(runEvent => runEvent.Seq));
        Assert.All(events, runEvent => Assert.True(runEvent.Synthetic));
        Assert.All(events, runEvent => Assert.Equal(record.InvocationId, runEvent.InvocationId));
    }

    [Fact]
    public void T_B3_7_History_walks_step_durations_forward_from_the_run_start()
    {
        var record = BuildRecord(MetaInvocationStatus.Completed, error: null);
        var session = BuildSession(
            new StepSpec("validate", ToolResultStatuses.Completed, 100),
            new StepSpec("fetch", ToolResultStatuses.Completed, 250));

        var events = MetaRunEventComposer.FromHistory(record, session);

        Assert.Equal(
            [MetaRunEventKinds.Claim, MetaRunEventKinds.StepCompleted, MetaRunEventKinds.StepCompleted, MetaRunEventKinds.Completed],
            events.Select(runEvent => runEvent.Kind));

        // Derived, but derived from recorded durations: 100ms then +250ms from the run start.
        Assert.Equal(record.CreatedAtUtc, events[0].AtUtc);
        Assert.Equal(Started.AddMilliseconds(100), events[1].AtUtc);
        Assert.Equal(Started.AddMilliseconds(350), events[2].AtUtc);
        Assert.Equal(Started.AddMinutes(1), events[3].AtUtc);
    }

    [Theory]
    [InlineData(MetaInvocationStatus.Completed, null, "completed", "Completed")]
    [InlineData(MetaInvocationStatus.Completed, "approval_denied", "failed", "Completed")]
    [InlineData(MetaInvocationStatus.Uncertain, "interrupted", "uncertain", "Uncertain")]
    public void T_B3_7_A_ledger_row_maps_onto_exactly_one_terminal_kind(
        MetaInvocationStatus status,
        string? error,
        string expectedKind,
        string expectedStatus)
    {
        var (kind, reportedStatus, detail) = MetaRunEventComposer.DescribeTerminal(BuildRecord(status, error));

        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedStatus, reportedStatus);
        Assert.Equal(error, detail);
    }

    [Fact]
    public void T_B3_7_The_last_run_of_the_right_skill_is_the_one_described()
    {
        var session = BuildSessionFor("triage-order", new StepSpec("validate", ToolResultStatuses.Completed, 5));
        session.MetaRunHistory.Add(new SessionMetaRunRecord
        {
            RunId = "run-later",
            SkillName = "other-skill",
            Status = "completed",
            StartedAtUtc = Started.AddMinutes(5),
            CompletedAtUtc = Started.AddMinutes(6),
            StepResults = [new SessionMetaStepResult { Id = "unrelated", Kind = "tool", Status = ToolResultStatuses.Completed }]
        });

        var events = MetaRunEventComposer.FromHistory(BuildRecord(MetaInvocationStatus.Completed, null), session);

        // The newest run is a different skill, so the run for this skill is still the one used.
        Assert.Contains(events, runEvent => runEvent.Kind == MetaRunEventKinds.StepCompleted && runEvent.StepId == "validate");
        Assert.DoesNotContain(events, runEvent => runEvent.StepId == "unrelated");
    }

    private static MetaInvocationRecord BuildRecord(MetaInvocationStatus status, string? error)
        => new(
            CallerId: "caller",
            IdempotencyKey: "key-composer",
            RequestHash: "hash",
            InvocationId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Status: status,
            Result: status == MetaInvocationStatus.Completed && error is null ? "done" : null,
            Error: error,
            CreatedAtUtc: Started,
            Pause: null,
            SessionId: "session-1",
            Skill: "triage-order");

    private static Session BuildSession(params StepSpec[] steps)
        => BuildSessionFor("triage-order", steps);

    private static Session BuildSessionFor(string skillName, params StepSpec[] steps)
    {
        var session = new Session
        {
            Id = "session-1",
            ChannelId = "test",
            SenderId = "sender-1"
        };
        session.MetaRunHistory.Add(new SessionMetaRunRecord
        {
            RunId = "run-1",
            SkillName = skillName,
            Status = "completed",
            StartedAtUtc = Started,
            CompletedAtUtc = Started.AddMinutes(1),
            StepResults = [.. steps.Select(step => new SessionMetaStepResult
            {
                Id = step.Id,
                Kind = "tool",
                Status = step.Status,
                FailureCode = step.FailureCode,
                DurationMs = step.DurationMs
            })]
        });
        return session;
    }

    private readonly record struct StepSpec(string Id, string Status, double DurationMs, string? FailureCode = null);
}
