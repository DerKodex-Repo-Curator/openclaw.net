namespace OpenClaw.Core.Abstractions;

/// <summary>
/// A refusal a tool hook wants the model to see.
/// </summary>
/// <param name="Message">What was refused and what the model should do instead. Never a raw payload.</param>
/// <param name="FailureCode">Optional <see cref="OpenClaw.Core.Models.ToolFailureCodes"/> value; defaults to <c>tool_failed</c>.</param>
/// <param name="NextStep">Optional operator-facing hint, surfaced on the tool result.</param>
public readonly record struct ToolHookDenial(
    string Message,
    string? FailureCode = null,
    string? NextStep = null);

/// <summary>
/// Optional companion to <see cref="IToolHookWithContext"/>: a hook that denies a call can also say
/// why, so the model receives something it can act on instead of a bare "denied by hook".
/// </summary>
/// <remarks>
/// Kept as a separate interface rather than a richer return type on
/// <see cref="IToolHookWithContext.BeforeExecuteAsync"/> so every existing hook keeps compiling
/// unchanged: a hook that has nothing to explain simply does not implement this.
/// </remarks>
public interface IToolHookDenialReason
{
    /// <summary>
    /// Explains why <paramref name="context"/> was just denied, or <see langword="null"/> when this
    /// hook has nothing to add. Implementations must derive the answer from the same state
    /// <see cref="IToolHookWithContext.BeforeExecuteAsync(ToolHookContext, CancellationToken)"/> used,
    /// so the explanation can never disagree with the decision.
    /// </summary>
    ToolHookDenial? ExplainDenial(ToolHookContext context);
}
