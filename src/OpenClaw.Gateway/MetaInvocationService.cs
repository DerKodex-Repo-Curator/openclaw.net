using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent;
using OpenClaw.Core.Approvals;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway;

public sealed class MetaInvocationService(
    MetaInvocationStore store,
    SessionManager sessionManager,
    IAgentRuntime agentRuntime,
    ILogger<MetaInvocationService> logger,
    IMetaApprovalStore? approvals = null)
{
    public async Task<MetaInvocationExecutionResult> InvokeAsync(
        string callerId,
        string? accountId,
        bool isAdmin,
        string idempotencyKey,
        MetaSkillInvocationRequest request,
        CancellationToken cancellationToken,
        MetaInvocationResumeRequest? resume = null)
    {
        var session = await sessionManager.LoadAsync(request.SessionId, cancellationToken);
        if (session is null)
            return new MetaInvocationExecutionResult(StatusCodes.Status404NotFound, Error: "Session not found.");

        if (!SessionAccess.CanWrite(session, accountId, isAdmin))
            return new MetaInvocationExecutionResult(StatusCodes.Status403Forbidden, Error: SessionAccess.DeniedMessage);

        session = await sessionManager.GetOrCreateByIdAsync(
            session.Id,
            session.ChannelId,
            session.SenderId,
            cancellationToken,
            session.OwnerAccountId);

        // The resume header is deliberately excluded from the request hash: a resume is the
        // continuation of the same logical invocation, not a different request.
        var requestJson = JsonSerializer.SerializeToUtf8Bytes(
            request,
            MetaInvocationJsonContext.Default.MetaSkillInvocationRequest);
        var requestHash = Convert.ToHexString(SHA256.HashData(requestJson));
        var claim = await store.BeginOrGetAsync(
            callerId,
            idempotencyKey,
            requestHash,
            cancellationToken,
            sessionId: request.SessionId,
            skill: request.Skill);

        if (claim.Kind == MetaInvocationClaimKind.Conflict)
        {
            return new MetaInvocationExecutionResult(
                StatusCodes.Status409Conflict,
                Error: "Idempotency-Key was already used for a different request.");
        }

        if (claim.Kind == MetaInvocationClaimKind.Existing)
        {
            // Without a resume header a repeated POST is a pure query: it never re-enters the run.
            if (resume is null || claim.Record.Status != MetaInvocationStatus.AwaitingInput)
                return ExistingResult(claim.Record);

            // Recorded before the decision is resolved: whoever reads the timeline next sees that a
            // human (or a retrying caller) asked to continue, and whether they brought an answer.
            await store.AppendEventAsync(
                claim.Record.InvocationId,
                [new MetaRunEventDraft(
                    MetaRunEventKinds.ResumeRequested,
                    claim.Record.Pause?.StepId,
                    nameof(MetaInvocationStatus.AwaitingInput),
                    resume.Inputs is null || resume.Inputs.Count == 0 ? "empty-inputs" : null)],
                CancellationToken.None);

            if (claim.Record.Pause?.ApprovalId is { Length: > 0 } approvalId)
            {
                var decision = await ResolveApprovalDecisionAsync(claim.Record, approvalId, cancellationToken);
                if (decision != MetaApprovalDecisionKind.Pending)
                {
                    // The journal records the decision before the run moves, so a timeline read at
                    // any instant explains why the run resumed (or stopped for good).
                    await store.AppendEventAsync(
                        claim.Record.InvocationId,
                        [new MetaRunEventDraft(
                            MetaRunEventKinds.ApprovalDecided,
                            claim.Record.Pause?.StepId,
                            nameof(MetaInvocationStatus.AwaitingInput),
                            decision == MetaApprovalDecisionKind.Denied ? "denied" : "approved")],
                        CancellationToken.None);
                }

                if (decision == MetaApprovalDecisionKind.Denied)
                {
                    session.MetaExecutionCheckpoint = null;
                    await sessionManager.PersistAsync(session, CancellationToken.None);
                    await store.CompleteWithErrorAsync(
                        callerId,
                        idempotencyKey,
                        MetaApprovalErrorCodes.Denied,
                        CancellationToken.None,
                        MetaRunEventComposer.DeriveStepsForTerminal(session, request.Skill));
                    var denied = await store.TryGetAsync(callerId, idempotencyKey, CancellationToken.None);
                    return new MetaInvocationExecutionResult(
                        StatusCodes.Status200OK,
                        ToResponse((denied ?? claim.Record) with
                        {
                            Status = MetaInvocationStatus.Completed,
                            Result = null,
                            Error = MetaApprovalErrorCodes.Denied,
                            Pause = null
                        }));
                }

                if (decision == MetaApprovalDecisionKind.Pending)
                    // Resuming without a decision would only park the same call again.
                    return ExistingResult(claim.Record);
            }

            var resumable = await store.TryBeginResumeAsync(callerId, idempotencyKey, cancellationToken);
            if (resumable is null)
            {
                // Another resume is already running (or the run finished meanwhile): report the
                // current state rather than failing the caller with a conflict.
                return ExistingResult(
                    await store.TryGetAsync(callerId, idempotencyKey, cancellationToken) ?? claim.Record);
            }

            return await ExecuteAsync(
                session,
                accountId,
                isAdmin,
                callerId,
                idempotencyKey,
                request,
                resumable,
                ResumeInput(resumable, resume),
                cancellationToken);
        }

        return await ExecuteAsync(
            session,
            accountId,
            isAdmin,
            callerId,
            idempotencyKey,
            request,
            claim.Record,
            request.Input,
            cancellationToken);
    }

    private async Task<MetaInvocationExecutionResult> ExecuteAsync(
        Session session,
        string? accountId,
        bool isAdmin,
        string callerId,
        string idempotencyKey,
        MetaSkillInvocationRequest request,
        MetaInvocationRecord record,
        string? input,
        CancellationToken cancellationToken)
    {
        var wasPaused = record.Status == MetaInvocationStatus.AwaitingInput;
        try
        {
            await using var sessionLock = await sessionManager.AcquireSessionLockAsync(session.Id, cancellationToken);
            if (!SessionAccess.CanWrite(session, accountId, isAdmin))
            {
                const string denied = "Session ownership changed before invocation execution.";
                var error = await TryMarkUncertainAsync(
                        callerId,
                        idempotencyKey,
                        denied,
                        record.InvocationId,
                        MetaRunEventComposer.DeriveStepsForTerminal(session, request.Skill))
                    ? denied
                    : $"{denied} Invocation state could not be persisted.";
                return new MetaInvocationExecutionResult(
                    StatusCodes.Status409Conflict,
                    ToResponse(record with { Status = MetaInvocationStatus.Uncertain, Error = error, Pause = null }, result: null));
            }

            var authenticatedUserId = session.AuthenticatedUserId;
            if (wasPaused)
            {
                // The run is about to execute again: a timeline that showed only `step.paused` would
                // leave the reader unable to tell "still waiting" from "resumed and silently stuck".
                await store.AppendEventAsync(
                    record.InvocationId,
                    [new MetaRunEventDraft(
                        MetaRunEventKinds.Resumed,
                        record.Pause?.StepId,
                        nameof(MetaInvocationStatus.Running))],
                    CancellationToken.None);
            }

            string result;
            try
            {
                session.AuthenticatedUserId = accountId;
                result = await agentRuntime.InvokeMetaSkillAsync(session, request.Skill, input, cancellationToken);
            }
            finally
            {
                session.AuthenticatedUserId = authenticatedUserId;
            }

            // A pause is a normal return value: the runtime keeps its durable checkpoint when a
            // step needs a human, so the pause is detected from the session, not from the text.
            var pause = TryDescribePause(session, request.Skill);
            try
            {
                await sessionManager.PersistAsync(session, CancellationToken.None, sessionLockHeld: true);
                if (pause is not null)
                {
                    var paused = await store.MarkAwaitingInputAsync(
                        callerId,
                        idempotencyKey,
                        pause,
                        CancellationToken.None,
                        MetaRunEventComposer.DeriveStepsForPause(session, request.Skill, pause.StepId));
                    logger.LogInformation(
                        "MetaSkill invocation {InvocationId} paused on step {StepId} ({Reason}); waiting for a resume.",
                        paused.InvocationId,
                        pause.StepId,
                        pause.Reason);
                    return new MetaInvocationExecutionResult(StatusCodes.Status200OK, ToResponse(paused, result: null));
                }

                await store.CompleteAsync(
                    callerId,
                    idempotencyKey,
                    result,
                    CancellationToken.None,
                    MetaRunEventComposer.DeriveStepsForTerminal(session, request.Skill));
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Failed to persist the session or completed result for MetaSkill invocation {InvocationId}.",
                    record.InvocationId);
                const string error = "MetaSkill execution completed, but its session or result could not be persisted; do not retry automatically.";
                var persisted = await TryMarkUncertainAsync(
                    callerId,
                    idempotencyKey,
                    error,
                    record.InvocationId,
                    MetaRunEventComposer.DeriveStepsForTerminal(session, request.Skill));
                var responseError = persisted
                    ? error
                    : $"{error} The uncertain state could not be persisted either.";
                return new MetaInvocationExecutionResult(
                    StatusCodes.Status409Conflict,
                    ToResponse(record with { Status = MetaInvocationStatus.Uncertain, Error = responseError, Pause = null }, result: null));
            }

            return new MetaInvocationExecutionResult(
                StatusCodes.Status200OK,
                ToResponse(record with { Status = MetaInvocationStatus.Completed, Result = result, Error = null, Pause = null }));
        }
        catch (Exception exception)
        {
            if (wasPaused && record.Pause is not null)
            {
                // The checkpoint is still durable, so the pause can be resumed again once the
                // failure is understood: never burn a human-gated invocation on one exception.
                logger.LogError(exception,
                    "Resumed MetaSkill invocation {InvocationId} failed; it stays paused.",
                    record.InvocationId);
                var pausedAgain = await TryMarkAwaitingInputAsync(callerId, idempotencyKey, record.Pause)
                    ?? await store.TryGetAsync(callerId, idempotencyKey, CancellationToken.None)
                    ?? record;
                return new MetaInvocationExecutionResult(StatusCodes.Status200OK, ToResponse(pausedAgain, result: null));
            }

            var error = exception is OperationCanceledException
                ? "MetaSkill execution was cancelled; this invocation will not be retried automatically."
                : $"MetaSkill execution failed ({exception.GetType().Name}); this invocation will not be retried automatically.";
            if (!await TryMarkUncertainAsync(
                    callerId,
                    idempotencyKey,
                    error,
                    record.InvocationId,
                    MetaRunEventComposer.DeriveStepsForTerminal(session, request.Skill)))
                error = $"{error} The uncertain state could not be persisted.";
            return new MetaInvocationExecutionResult(
                StatusCodes.Status409Conflict,
                ToResponse(record with { Status = MetaInvocationStatus.Uncertain, Error = error, Pause = null }, result: null));
        }
    }

    private async Task<MetaApprovalDecisionKind> ResolveApprovalDecisionAsync(
        MetaInvocationRecord record,
        string approvalId,
        CancellationToken cancellationToken)
    {
        if (approvals is null)
            return MetaApprovalDecisionKind.Pending;

        var approval = await approvals.TryGetAsync(approvalId, cancellationToken);
        if (approval is null)
        {
            // Expired or swept: re-entering the run re-creates the approval (deterministic id).
            logger.LogWarning(
                "Meta approval {ApprovalId} for invocation {InvocationId} is gone; the run will re-request it.",
                approvalId,
                record.InvocationId);
            return MetaApprovalDecisionKind.Approved;
        }

        if (string.Equals(approval.Decision, MetaApprovalDecisions.Denied, StringComparison.Ordinal))
            return MetaApprovalDecisionKind.Denied;
        return approval.IsPending ? MetaApprovalDecisionKind.Pending : MetaApprovalDecisionKind.Approved;
    }

    /// <summary>
    /// Maps a resume payload onto the runtime's single answer slot. The human answered the step
    /// the checkpoint is blocked on; values for other steps are ignored.
    /// </summary>
    private string? ResumeInput(MetaInvocationRecord record, MetaInvocationResumeRequest resume)
    {
        var inputs = resume.Inputs;
        if (inputs is null || inputs.Count == 0)
            return null;

        var pendingStepId = record.Pause?.StepId;
        if (!string.IsNullOrWhiteSpace(pendingStepId) &&
            inputs.TryGetValue(pendingStepId, out var answer) &&
            !string.IsNullOrWhiteSpace(answer))
        {
            return answer;
        }

        logger.LogWarning(
            "Resume payload for meta invocation {InvocationId} carried no value for pending step {StepId}; resuming without an answer.",
            record.InvocationId,
            pendingStepId);
        return null;
    }

    private static MetaInvocationPauseInfo? TryDescribePause(Session session, string skillName)
    {
        var checkpoint = session.MetaExecutionCheckpoint;
        if (checkpoint is null || string.IsNullOrWhiteSpace(checkpoint.PendingStepId))
            return null;
        if (!string.Equals(checkpoint.SkillName, skillName, StringComparison.OrdinalIgnoreCase))
            return null;

        var reason = string.IsNullOrWhiteSpace(checkpoint.Reason) ? "user_input" : checkpoint.Reason;
        return new MetaInvocationPauseInfo(reason, checkpoint.PendingStepId, checkpoint.Prompt, checkpoint.ApprovalId);
    }

    private static MetaInvocationExecutionResult ExistingResult(MetaInvocationRecord record)
    {
        var statusCode = record.Status switch
        {
            MetaInvocationStatus.Running => StatusCodes.Status202Accepted,
            MetaInvocationStatus.Completed => StatusCodes.Status200OK,
            MetaInvocationStatus.AwaitingInput => StatusCodes.Status200OK,
            MetaInvocationStatus.Uncertain => StatusCodes.Status409Conflict,
            _ => throw new InvalidOperationException($"Unsupported meta invocation status '{record.Status}'.")
        };
        return new MetaInvocationExecutionResult(statusCode, ToResponse(record));
    }

    private async Task<MetaInvocationRecord?> TryMarkAwaitingInputAsync(
        string callerId,
        string idempotencyKey,
        MetaInvocationPauseInfo pause)
    {
        try
        {
            return await store.MarkAwaitingInputAsync(callerId, idempotencyKey, pause, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Failed to persist the paused state of a resumed MetaSkill invocation (step {StepId}).",
                pause.StepId);
            return null;
        }
    }

    private async Task<bool> TryMarkUncertainAsync(
        string callerId,
        string idempotencyKey,
        string error,
        Guid invocationId,
        IReadOnlyList<MetaRunEventDraft>? stepEvents = null)
    {
        try
        {
            await store.MarkUncertainAsync(
                callerId, idempotencyKey, error, CancellationToken.None, stepEvents);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Failed to persist the uncertain state for MetaSkill invocation {InvocationId}.",
                invocationId);
            return false;
        }
    }

    private static MetaInvocationResponse ToResponse(
        MetaInvocationRecord record,
        string? result = null,
        string? error = null)
        => new(
            record.InvocationId,
            record.Status,
            result ?? record.Result,
            error ?? record.Error,
            record.CreatedAtUtc,
            record.Pause);
}
