namespace OpenClaw.Core.Models;

/// <summary>
/// The shared vocabulary for "this delegated result is real work, but it is not all of the work".
/// </summary>
/// <remarks>
/// <para>
/// A sub-agent that runs out of iterations would otherwise return either a bare limit notice or a
/// plausible-looking summary that silently omits the part it never got to. Neither is usable: the
/// parent cannot tell "done" from "stopped", so it reports success for half a task.
/// </para>
/// <para>
/// The marker lives in the result <em>text</em> on purpose. The parent agent reads that text, and so
/// does any human looking at the transcript, so the caveat travels with the result instead of
/// depending on a field that one of them might not read.
/// </para>
/// </remarks>
public static class DelegationCompleteness
{
    /// <summary>Prefix of the notice appended to an incomplete delegated result.</summary>
    public const string MarkerPrefix = "[DELEGATION_INCOMPLETE:";

    /// <summary>
    /// Instruction injected for the reserved final model call. It names the two things a truncated
    /// answer usually omits: what was not done, and how confident the rest is.
    /// </summary>
    public const string FinalCallInstruction =
        "This is the final model call available for this delegated task. Do not call tools. " +
        "Return the most useful answer supported by the results already gathered. " +
        "State explicitly which parts of the task you did not complete, which sources or tools " +
        "were unavailable, and what remains uncertain. Never answer with only a notice that a " +
        "limit was reached.";

    /// <summary>
    /// Appends the marker to <paramref name="text"/>. Idempotent: a result that already carries the
    /// marker is returned unchanged, so re-wrapping a summary cannot stack notices.
    /// </summary>
    public static string AppendNotice(string text, int iterationsUsed)
    {
        if (HasNotice(text))
            return text;

        var trimmed = (text ?? string.Empty).TrimEnd();
        var notice =
            $"{MarkerPrefix} this result stopped at the delegated task's iteration limit " +
            $"({iterationsUsed} model calls). Treat it as partial: check which parts of the task are " +
            "still uncovered before relying on it.]";

        return trimmed.Length == 0 ? notice : trimmed + "\n\n" + notice;
    }

    /// <summary>True when <paramref name="text"/> already carries the marker.</summary>
    public static bool HasNotice(string? text)
        => text is not null && text.Contains(MarkerPrefix, StringComparison.Ordinal);
}
