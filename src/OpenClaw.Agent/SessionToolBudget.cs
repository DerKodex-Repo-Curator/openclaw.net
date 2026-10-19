using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

/// <summary>
/// Counts the tool calls a contract's <see cref="ContractPolicy.MaxToolCalls"/> is meant to bound.
/// </summary>
public static class SessionToolBudget
{
    /// <summary>
    /// Tool calls made since the session's last user message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The budget is per turn, not per session. The default session key is
    /// <c>channelId:senderId</c>, so a session naturally lives for days: counting across the whole
    /// history meant a long-lived conversation permanently lost the ability to call tools once it
    /// had ever made that many calls, with no signal to the user about why.
    /// </para>
    /// <para>
    /// Walks backwards by index rather than with LINQ: a session history that is being appended to
    /// while a tool hook runs would make an enumerator throw "Collection was modified", and an index
    /// scan only ever sees a prefix.
    /// </para>
    /// </remarks>
    public static int CountToolCallsInCurrentTurn(Session? session)
    {
        if (session is null) return 0;

        var calls = 0;
        var history = session.History;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var turn = history[i];
            if (turn.ToolCalls is { Count: > 0 })
                calls += turn.ToolCalls.Count;
            if (string.Equals(turn.Role, "user", StringComparison.Ordinal))
                break;
        }

        return calls;
    }
}
