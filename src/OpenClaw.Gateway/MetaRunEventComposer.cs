using OpenClaw.Core.Models;
using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway;

/// <summary>
/// Turns what the gateway can observe about a run into run events.
/// </summary>
/// <remarks>
/// <para>
/// The hard truth this class exists to keep honest: neither <see cref="OpenClaw.Agent.IAgentRuntime"/>
/// implementation exposes a step boundary as it happens — step results are materialised when the run
/// returns (or pauses). So the gateway cannot timestamp <c>step.started</c> or <c>step.completed</c>
/// as they occur, and inventing a time would make the timeline lie about durations.
/// </para>
/// <para>
/// What it can do is derive the step timeline from the run record at the two moments it is
/// observable (pause and terminal) and mark those events <c>synthetic</c>, which is exactly what the
/// contract's <c>synthetic</c> flag is for. <c>step.paused</c> — the event an operator actually reads
/// — is real: it is emitted at the pause write with the pause's own timestamp.
/// </para>
/// </remarks>
internal static class MetaRunEventComposer
{
    /// <summary>
    /// Step events observed at a pause: the steps the run already finished, then the one it is
    /// blocked on. Synthetic, deduplicated by the store, so a second pause cannot re-emit them.
    /// </summary>
    public static IReadOnlyList<MetaRunEventDraft> DeriveStepsForPause(
        Session? session,
        string? skillName,
        string? pendingStepId)
    {
        var drafts = new List<MetaRunEventDraft>();
        AppendCompletedSteps(drafts, session, skillName);

        if (!string.IsNullOrWhiteSpace(pendingStepId))
        {
            drafts.Add(new MetaRunEventDraft(
                MetaRunEventKinds.StepStarted,
                pendingStepId,
                "Running",
                Detail: null,
                Synthetic: true));
        }

        return drafts;
    }

    /// <summary>Step events observed at a terminal write, derived from the finished run record.</summary>
    public static IReadOnlyList<MetaRunEventDraft> DeriveStepsForTerminal(Session? session, string? skillName)
    {
        var drafts = new List<MetaRunEventDraft>();
        AppendCompletedSteps(drafts, session, skillName);
        return drafts;
    }

    private static void AppendCompletedSteps(
        List<MetaRunEventDraft> drafts,
        Session? session,
        string? skillName)
    {
        var run = LastRun(session, skillName);
        if (run is null)
            return;

        foreach (var step in run.StepResults)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
                continue;

            var status = string.IsNullOrWhiteSpace(step.Status) ? ToolResultStatuses.Completed : step.Status;
            var kind = string.Equals(status, ToolResultStatuses.Completed, StringComparison.Ordinal)
                ? MetaRunEventKinds.StepCompleted
                : MetaRunEventKinds.StepFailed;
            drafts.Add(new MetaRunEventDraft(kind, step.Id, status, step.FailureCode, Synthetic: true));
        }
    }

    /// <summary>
    /// Reconstructs a coarse timeline for an invocation that has no journal — either it ran before
    /// B3 shipped, or its journal was lost. Every event is synthetic because every timestamp here is
    /// derived (the run record only carries per-step <c>DurationMs</c>), and the caller reports
    /// <c>synthetic=true</c> on the whole page.
    /// </summary>
    public static IReadOnlyList<MetaRunEvent> FromHistory(MetaInvocationRecord record, Session? session)
    {
        ArgumentNullException.ThrowIfNull(record);

        var events = new List<MetaRunEvent>();
        var seq = 1L;

        void Add(DateTimeOffset atUtc, string kind, string? stepId, string status, string? detail)
            => events.Add(new MetaRunEvent(record.InvocationId, seq++, atUtc, kind, stepId, status, detail, Synthetic: true));

        Add(record.CreatedAtUtc, MetaRunEventKinds.Claim, null, nameof(MetaInvocationStatus.Running), null);

        var run = LastRun(session, record.Skill);
        var cursor = run?.StartedAtUtc ?? record.CreatedAtUtc;
        if (run is not null)
        {
            foreach (var step in run.StepResults)
            {
                if (string.IsNullOrWhiteSpace(step.Id))
                    continue;

                cursor = cursor.AddMilliseconds(Math.Max(0, step.DurationMs));
                var status = string.IsNullOrWhiteSpace(step.Status) ? ToolResultStatuses.Completed : step.Status;
                var kind = string.Equals(status, ToolResultStatuses.Completed, StringComparison.Ordinal)
                    ? MetaRunEventKinds.StepCompleted
                    : MetaRunEventKinds.StepFailed;
                Add(cursor, kind, step.Id, status, step.FailureCode);
            }
        }

        var (kind2, status2, detail2) = DescribeTerminal(record);
        Add(run?.CompletedAtUtc ?? record.CreatedAtUtc, kind2, run?.StepResults.LastOrDefault()?.Id, status2, detail2);

        return events;
    }

    /// <summary>Maps a ledger row onto the single terminal event kind it ends with.</summary>
    public static (string Kind, string Status, string? Detail) DescribeTerminal(MetaInvocationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record switch
        {
            { Status: MetaInvocationStatus.Uncertain } =>
                (MetaRunEventKinds.Uncertain, nameof(MetaInvocationStatus.Uncertain), record.Error),
            { Status: MetaInvocationStatus.Completed, Error: not null } =>
                (MetaRunEventKinds.Failed, nameof(MetaInvocationStatus.Completed), record.Error),
            { Status: MetaInvocationStatus.Completed } =>
                (MetaRunEventKinds.Completed, nameof(MetaInvocationStatus.Completed), null),
            // A paused or running row has no terminal event yet; the caller only asks for a terminal
            // description when it has one, so this branch is a defensive default.
            _ => (MetaRunEventKinds.StepPaused, record.Status.ToString(), record.Pause?.Reason)
        };
    }

    private static SessionMetaRunRecord? LastRun(Session? session, string? skillName)
    {
        if (session is null || session.MetaRunHistory.Count == 0)
            return null;

        for (var index = session.MetaRunHistory.Count - 1; index >= 0; index--)
        {
            var run = session.MetaRunHistory[index];
            if (string.IsNullOrWhiteSpace(skillName) ||
                string.Equals(run.SkillName, skillName, StringComparison.OrdinalIgnoreCase))
            {
                return run;
            }
        }

        return null;
    }
}
