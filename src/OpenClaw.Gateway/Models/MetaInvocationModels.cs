using System.Text.Json.Serialization;

namespace OpenClaw.Gateway.Models;

[JsonConverter(typeof(JsonStringEnumConverter<MetaInvocationStatus>))]
public enum MetaInvocationStatus
{
    Running,
    Completed,
    Uncertain,

    /// <summary>
    /// The run reached a step that needs a human (user input or a tool approval) and its
    /// durable execution checkpoint was persisted. The invocation is not an error: it is
    /// waiting to be resumed with the same idempotency key.
    /// </summary>
    AwaitingInput
}

/// <summary>
/// Why an invocation is paused. <see cref="ApprovalId"/> is set for
/// <c>reason = "tool_approval"</c> and points at the persisted meta approval record.
/// </summary>
public sealed record MetaInvocationPauseInfo(
    string Reason,
    string StepId,
    string? Prompt,
    string? ApprovalId);

/// <summary>
/// One ledger row. <see cref="SessionId"/> and <see cref="Skill"/> were added with B3: the run index
/// filters by session, and the event journal needs them to reconstruct a timeline for invocations
/// that predate the journal. Older records deserialize with both null.
/// </summary>
/// <param name="EventsGap">
/// Set when the journal could not be written for this run. The ledger still advanced — the run
/// happened — so the honest state is "the timeline may be missing events", not a rolled-back run.
/// </param>
public sealed record MetaInvocationRecord(
    string CallerId,
    string IdempotencyKey,
    string RequestHash,
    Guid InvocationId,
    MetaInvocationStatus Status,
    string? Result,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    MetaInvocationPauseInfo? Pause = null,
    string? SessionId = null,
    string? Skill = null,
    bool EventsGap = false);

public enum MetaInvocationClaimKind
{
    Started,
    Existing,
    Conflict
}

public sealed record MetaInvocationClaim(MetaInvocationClaimKind Kind, MetaInvocationRecord Record);

public sealed record MetaSkillInvocationRequest(string Skill, string? Input, string SessionId);

/// <summary>Continuation payload of the <c>X-OpenClaw-Resume</c> header.</summary>
public sealed record MetaInvocationResumeRequest(IReadOnlyDictionary<string, string>? Inputs);

public sealed record MetaInvocationResponse(
    Guid InvocationId,
    MetaInvocationStatus Status,
    string? Result,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    MetaInvocationPauseInfo? Pause = null);

public sealed record MetaInvocationExecutionResult(
    int StatusCode,
    MetaInvocationResponse? Response = null,
    string? Error = null);

/// <summary>Approval list item: the argument body is deliberately not echoed.</summary>
public sealed record MetaApprovalSummary(
    string ApprovalId,
    string SessionId,
    string SkillName,
    string StepId,
    string ToolName,
    string? Summary,
    string ArgsFingerprint,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? Decision,
    string? DecidedBy,
    DateTimeOffset? DecidedAtUtc);

public sealed record MetaApprovalDecisionRequest(bool Approved, string? Reason);

public sealed record MetaApprovalReceipt(
    string ApprovalId,
    bool Approved,
    string DecidedBy,
    DateTimeOffset DecidedAtUtc,
    string? Reason);
