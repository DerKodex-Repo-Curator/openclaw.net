using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

/// <summary>
/// Builds the provider-facing message list from a session's history.
/// </summary>
/// <remarks>
/// <para>
/// One implementation for both runtimes. Before this existed <see cref="AgentRuntime"/> and
/// <c>MafAgentRuntime</c> each had their own copy, and the MAF copy never turned markers into content
/// parts at all — so the same session produced a multimodal request on one runtime and a prompt full
/// of <c>[IMAGE_URL:...]</c> text on the other. Sharing the composer makes that class of divergence
/// structural rather than a thing to remember.
/// </para>
/// <para>
/// <b>Budget direction.</b> Turns are prepared newest-first even though messages are emitted oldest-first.
/// With a 2-turn window and a 20 MB ceiling, two 15 MB images cannot both fit; the newest one must
/// win, because the oldest is usually the one the user is referring back to ("that picture again"),
/// which is recoverable with a follow-up, whereas dropping the current question is not.
/// </para>
/// </remarks>
public sealed class ChatMessageComposer
{
    private readonly AttachmentInlinePolicy _policy;
    private readonly AttachmentInliner _inliner;
    private readonly ILogger? _logger;

    public ChatMessageComposer(
        AttachmentInlinePolicy? policy = null,
        IAttachmentByteSource? byteSource = null,
        ILogger? logger = null)
    {
        _policy = policy ?? AttachmentInlinePolicy.Default;
        _inliner = new AttachmentInliner(_policy, byteSource);
        _logger = logger;
    }

    /// <summary>The effective policy, useful for diagnostics and for the write path's rollback flag.</summary>
    public AttachmentInlinePolicy Policy => _policy;

    /// <summary>
    /// Session history → chat messages, without the system message (callers own their own preamble).
    /// </summary>
    /// <param name="session">Session whose <see cref="Session.History"/> is being read.</param>
    /// <param name="maxHistoryTurns">History window, counted in turns.</param>
    /// <param name="exactLatestToolBatch">
    /// When true the newest tool batch is emitted as real <see cref="FunctionCallContent"/> /
    /// <see cref="FunctionResultContent"/> pairs instead of a summarized text block. Needed to resume a
    /// checkpoint; <c>MafAgentRuntime</c> passes false because it never resumes through this path.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<List<ChatMessage>> ComposeHistoryAsync(
        Session session,
        int maxHistoryTurns,
        bool exactLatestToolBatch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        var history = session.History;
        var skip = Math.Max(0, history.Count - Math.Max(0, maxHistoryTurns));
        var inWindow = ComputeWindow(history, skip);

        var prepared = await PrepareAsync(session, history, skip, inWindow, ct).ConfigureAwait(false);

        var messages = new List<ChatMessage>(history.Count - skip);
        for (var i = skip; i < history.Count; i++)
        {
            var turn = history[i];

            if (turn.Role == "system" && turn.Content.StartsWith("[Previous conversation summary:", StringComparison.Ordinal))
            {
                messages.Add(new ChatMessage(ChatRole.System, turn.Content));
            }
            else if (turn.Role is "user" or "assistant" && turn.Content != "[tool_use]")
            {
                messages.Add(new ChatMessage(
                    turn.Role == "user" ? ChatRole.User : ChatRole.Assistant,
                    BuildTurnContents(turn, i, prepared)));
            }
            else if (turn.Content == "[tool_use]" && turn.ToolCalls is { Count: > 0 })
            {
                if (exactLatestToolBatch && i == history.Count - 1)
                    AppendExactToolBatch(messages, turn);
                else
                    AppendSummarizedToolBatch(messages, turn);
            }
        }

        return messages;
    }

    /// <summary>
    /// Marks which turns are inside the inlining window.
    /// </summary>
    /// <remarks>
    /// Counted in <em>user turns</em>, backwards from the newest. Assistant turns and tool batches do
    /// not consume window slots: a single agent turn can produce ten history entries, and counting
    /// those would shrink a "2 turn" window to "whatever happened in the last few iterations".
    /// </remarks>
    private bool[] ComputeWindow(IReadOnlyList<ChatTurn> history, int skip)
    {
        var inWindow = new bool[history.Count];
        if (!_policy.ReferenceMode || _policy.InlineRecentUserTurns <= 0)
            return inWindow;

        var remaining = _policy.InlineRecentUserTurns;
        for (var i = history.Count - 1; i >= skip && remaining > 0; i--)
        {
            if (!IsUserTurn(history[i]))
                continue;

            inWindow[i] = true;
            remaining--;
        }

        return inWindow;
    }

    private async ValueTask<Dictionary<int, IList<AIContent>>> PrepareAsync(
        Session session,
        IReadOnlyList<ChatTurn> history,
        int skip,
        bool[] inWindow,
        CancellationToken ct)
    {
        var prepared = new Dictionary<int, IList<AIContent>>();
        if (!_policy.ReferenceMode)
            return prepared;

        var budget = new AttachmentInlineBudget(_policy);

        // Newest first: see the class remarks about budget direction.
        for (var i = history.Count - 1; i >= skip; i--)
        {
            var turn = history[i];
            if (turn.Attachments is not { Count: > 0 } attachments)
                continue;

            prepared[i] = await _inliner
                .InlineAsync(attachments, inWindow[i], budget, ct)
                .ConfigureAwait(false);
        }

        if (budget.DegradedCount > 0)
        {
            _logger?.LogDebug(
                "Attachment inlining for session {SessionId}: inlined={Inlined} degraded={Degraded} bytesLeft={BytesLeft}",
                session.Id,
                budget.InlinedCount,
                budget.DegradedCount,
                budget.RemainingBytes);
        }

        return prepared;
    }

    private IList<AIContent> BuildTurnContents(ChatTurn turn, int index, Dictionary<int, IList<AIContent>> prepared)
    {
        // Rollback switch: rebuild the marker lines and hand them to the pre-reference renderer. The
        // result is the same UriContent/TextContent sequence the old code produced, file:// URIs and
        // all — that is the point of a rollback, not a regression to fix.
        if (!_policy.ReferenceMode)
            return AttachmentContentRenderer.Build(AttachmentReferences.RenderMarkerText(turn), null);

        if (turn.Attachments is not { Count: > 0 })
            return AttachmentContentRenderer.Build(turn.Content, null);

        var contents = new List<AIContent>((prepared.TryGetValue(index, out var inline) ? inline.Count : 0) + 1);

        if (!string.IsNullOrWhiteSpace(turn.Content))
            contents.Add(new TextContent(turn.Content));

        if (prepared.TryGetValue(index, out var inlined))
            contents.AddRange(inlined);

        // A turn whose text was empty and whose references all rendered to nothing still has to send
        // something; this mirrors the pre-reference fallback.
        if (contents.Count == 0)
            contents.Add(new TextContent(turn.Content));

        return contents;
    }

    private static bool IsUserTurn(ChatTurn turn)
        => turn.Role == "user" && turn.Content != "[tool_use]";

    private static void AppendExactToolBatch(List<ChatMessage> messages, ChatTurn turn)
    {
        var callContents = new List<AIContent>(turn.ToolCalls!.Count);
        var resultContents = new List<AIContent>(turn.ToolCalls.Count);
        for (var toolIndex = 0; toolIndex < turn.ToolCalls.Count; toolIndex++)
        {
            var invocation = turn.ToolCalls[toolIndex];
            var callId = AgentCheckpointManager.ResolveCheckpointCallId(invocation, toolIndex);
            callContents.Add(new FunctionCallContent(
                callId,
                invocation.ToolName,
                AgentCheckpointManager.DeserializeToolArguments(invocation.Arguments)));
            resultContents.Add(new FunctionResultContent(callId, invocation.Result ?? ""));
        }

        messages.Add(new ChatMessage(ChatRole.Assistant, callContents));
        messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
    }

    private static void AppendSummarizedToolBatch(List<ChatMessage> messages, ChatTurn turn)
    {
        var toolSummary = string.Join("\n", turn.ToolCalls!.Select(tc =>
            $"- Called {tc.ToolName}: {AgentPromptContextAssembler.Truncate(tc.Result ?? "(no result)", 200)}"));
        messages.Add(new ChatMessage(ChatRole.Assistant, $"[Previous tool calls:\n{toolSummary}]"));
    }
}
