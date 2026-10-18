namespace OpenClaw.Core.Models;

/// <summary>
/// The closed set of run event kinds (design §6.1). Events are labels for humans, not an extension
/// point: a client that meets an unknown kind must ignore it, and nothing downstream switches on
/// kind in a way that would break if one were added.
/// </summary>
public static class MetaRunEventKinds
{
    public const string Claim = "claim";
    public const string ResumeRequested = "resume.requested";
    public const string Resumed = "resumed";
    public const string StepStarted = "step.started";
    public const string StepCompleted = "step.completed";
    public const string StepFailed = "step.failed";
    public const string StepPaused = "step.paused";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalDecided = "approval.decided";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Uncertain = "uncertain";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        Claim, ResumeRequested, Resumed, StepStarted, StepCompleted, StepFailed,
        StepPaused, ApprovalRequested, ApprovalDecided, Completed, Failed, Uncertain
    };

    public static bool IsKnown(string? kind) => kind is not null && Known.Contains(kind);

    /// <summary>Terminal kinds end the run; the stream closes after one of them.</summary>
    public static bool IsTerminal(string? kind) =>
        kind is Completed or Failed or Uncertain;
}

/// <summary>
/// One run event. <paramref name="Seq"/> is per invocation starting at 1; <paramref name="Detail"/>
/// is a short redacted label (an error code, a pause reason, a stable approval short id) and never
/// carries input or output.
/// </summary>
/// <param name="Synthetic">
/// True when the gateway did not observe the boundary live and derived this event from the run
/// record afterwards. The timestamps of synthetic events are observation times, not execution
/// times, so a client must not read duration out of them.
/// </param>
public sealed record MetaRunEvent(
    Guid InvocationId,
    long Seq,
    DateTimeOffset AtUtc,
    string Kind,
    string? StepId,
    string? Status,
    string? Detail,
    bool Synthetic = false)
{
    /// <summary>Ceiling for <see cref="Detail"/>; longer text is truncated, not stored.</summary>
    public const int MaxDetailLength = 512;
}

/// <summary>
/// <c>GET .../events</c> payload. <paramref name="NextSeq"/> is the highest sequence the caller may
/// pass as <c>after</c>; <paramref name="Terminal"/> says the timeline will not grow.
/// </summary>
public sealed record MetaRunEventPage(
    Guid InvocationId,
    IReadOnlyList<MetaRunEvent> Events,
    long NextSeq,
    bool Terminal,
    bool Gap,
    bool Synthetic);

/// <summary>The last frame of the event stream: the run reached a terminal state.</summary>
public sealed record MetaRunEventStreamTerminal(Guid InvocationId, string Status, long NextSeq);

/// <summary>One row of the run index. Deliberately flat: the page needs no detail to list it.</summary>
public sealed record MetaInvocationSummary(
    Guid InvocationId,
    string? SessionId,
    string? Skill,
    string Status,
    DateTimeOffset CreatedAtUtc,
    string? PauseReason,
    string? PauseStepId,
    string? Error);

/// <summary><c>GET .../meta-invocations</c> payload: keyset page over (CreatedAtUtc, InvocationId).</summary>
public sealed record MetaInvocationListResponse(
    IReadOnlyList<MetaInvocationSummary> Items,
    string? NextCursor);
