namespace OpenClaw.Core.Approvals;

/// <summary>
/// Human decision values stored on an approval record. Anything else (including null) is pending.
/// </summary>
public static class MetaApprovalDecisions
{
    public const string Approved = "approved";
    public const string Denied = "denied";
}

/// <summary>
/// Structured failure code reported when a run stops because a tool needs a human decision.
/// </summary>
public static class MetaApprovalErrorCodes
{
    public const string Denied = "approval.denied";
}

/// <summary>
/// A meta-skill step that needs a human decision before it may execute a tool. The record is
/// durable: it survives restarts and is resumed by re-entering the paused run.
/// </summary>
public sealed record MetaApprovalRecord(
    string ApprovalId,
    string SessionId,
    string CallerId,
    string SkillName,
    string StepId,
    string ToolName,
    string ArgumentsJson,
    string ArgsFingerprint,
    string? Summary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? Decision,
    string? DecidedBy,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionReason,
    DateTimeOffset? ConsumedAtUtc)
{
    public bool IsPending => string.IsNullOrWhiteSpace(Decision);
}

/// <summary>Everything needed to create (or re-find) the approval of one tool call.</summary>
public sealed record MetaApprovalRequest(
    string SessionId,
    string CallerId,
    string SkillName,
    string StepId,
    string ToolName,
    string ArgumentsJson,
    string ArgsFingerprint,
    string? Summary);

/// <summary>
/// Identifies the meta step that is executing a tool, so the executor can park the call instead
/// of auto-denying it. Passed explicitly per call: parallel waves share one turn context.
/// </summary>
public sealed record MetaApprovalContext(string SkillName, string StepId, string CallerId);

public enum MetaApprovalDecideStatus
{
    Recorded,
    NotFound,
    AlreadyDecided,
    Forbidden
}

public enum MetaApprovalDecisionKind
{
    Pending,
    Approved,
    Denied
}

public interface IMetaApprovalStore
{
    /// <summary>Deterministic id: the same tool call in the same step never queues twice.</summary>
    string ComputeApprovalId(MetaApprovalRequest request);

    Task<MetaApprovalRecord> GetOrCreateAsync(MetaApprovalRequest request, CancellationToken cancellationToken);

    Task<MetaApprovalRecord?> TryGetAsync(string approvalId, CancellationToken cancellationToken);

    Task<MetaApprovalDecideStatus> TryDecideAsync(
        string approvalId,
        bool approved,
        string decidedBy,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>An approved, unexpired decision for exactly this tool call.</summary>
    Task<MetaApprovalRecord?> FindActiveGrantAsync(
        string sessionId,
        string toolName,
        string argsFingerprint,
        CancellationToken cancellationToken);

    /// <summary>Audit marker only: a consumed grant never blocks a re-entrant resume.</summary>
    Task MarkConsumedAsync(string approvalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MetaApprovalRecord>> ListAsync(
        string callerId,
        string? sessionId,
        bool pendingOnly,
        CancellationToken cancellationToken);
}
