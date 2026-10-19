using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// The tool-call budget is per turn, and a refusal has to be legible enough for the model to recover.
/// Both properties are what keeps a long-lived session from slowly losing its tools for no stated reason.
/// </summary>
public sealed class ToolCallBudgetTests
{
    [Fact]
    public void CountToolCallsInCurrentTurn_IgnoresEarlierTurns()
    {
        var session = NewSession();
        AddToolTurn(session, "web_search", "web_search", "web_search");
        AddUserTurn(session, "second question");
        AddToolTurn(session, "file_read");

        Assert.Equal(1, SessionToolBudget.CountToolCallsInCurrentTurn(session));
    }

    [Fact]
    public void CountToolCallsInCurrentTurn_CountsWholeTurnWhenNoUserMessageSeen()
    {
        // Spawned and webhook sessions have no user turn; the budget then covers the whole history,
        // which is the same thing as "the turn so far".
        var session = NewSession();
        AddToolTurn(session, "a", "b");

        Assert.Equal(2, SessionToolBudget.CountToolCallsInCurrentTurn(session));
    }

    [Fact]
    public void CountToolCallsInCurrentTurn_ReturnsZeroForMissingSession()
        => Assert.Equal(0, SessionToolBudget.CountToolCallsInCurrentTurn(null));

    [Fact]
    public void CountToolCallsInCurrentTurn_ToolResultsDoNotCount()
    {
        var session = NewSession();
        AddUserTurn(session, "question");
        session.History.Add(new ChatTurn { Role = "tool", Content = "result" });

        Assert.Equal(0, SessionToolBudget.CountToolCallsInCurrentTurn(session));
    }

    [Fact]
    public void ExhaustedBudget_ExplainsItselfInsteadOfBareDenial()
    {
        var policy = new ContractPolicy { Id = "ctr_test", MaxToolCalls = 3 };
        var hook = new ContractScopeHook(_ => policy, _ => 3, NullLogger.Instance);

        var denial = hook.ExplainDenial(Context("web_search"));

        Assert.NotNull(denial);
        Assert.Equal(ToolFailureCodes.ToolCallBudgetExhausted, denial!.Value.FailureCode);
        Assert.Contains("3", denial.Value.Message, StringComparison.Ordinal);
        Assert.Contains("already been made", denial.Value.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(denial.Value.NextStep));
    }

    [Fact]
    public void AllowedCall_HasNoDenialToExplain()
    {
        var policy = new ContractPolicy { Id = "ctr_test", MaxToolCalls = 3 };
        var hook = new ContractScopeHook(_ => policy, _ => 1, NullLogger.Instance);

        Assert.Null(hook.ExplainDenial(Context("web_search")));
    }

    [Fact]
    public void ScopeDenial_IsExplainedWithoutEchoingThePath()
    {
        var allowed = Path.Combine(Path.GetTempPath(), $"openclaw-scope-{Guid.NewGuid():N}", "allowed");
        var denied = Path.Combine(Path.GetTempPath(), $"openclaw-scope-{Guid.NewGuid():N}", "denied", "file.txt");
        var policy = new ContractPolicy
        {
            Id = "ctr_test",
            ScopedCapabilities = [new ScopedCapability { ToolName = "file_read", AllowedPaths = [allowed] }]
        };
        var hook = new ContractScopeHook(_ => policy, _ => 0, NullLogger.Instance);

        var context = Context("file_read") with
        {
            ArgumentsJson = System.Text.Json.JsonSerializer.Serialize(new { path = denied })
        };

        var denial = hook.ExplainDenial(context);

        Assert.NotNull(denial);
        Assert.DoesNotContain(denied, denial!.Value.Message, StringComparison.Ordinal);
        Assert.Contains("outside the folders", denial.Value.Message, StringComparison.Ordinal);
    }

    private static Session NewSession() => new()
    {
        Id = "s1",
        ChannelId = "ws",
        SenderId = "u1"
    };

    private static ToolHookContext Context(string toolName) => new()
    {
        SessionId = "s1",
        ChannelId = "ws",
        SenderId = "u1",
        CorrelationId = "c1",
        ToolName = toolName,
        ArgumentsJson = "{}",
        IsStreaming = false
    };

    private static void AddUserTurn(Session session, string text)
        => session.History.Add(new ChatTurn { Role = "user", Content = text });

    private static void AddToolTurn(Session session, params string[] toolNames)
        => session.History.Add(new ChatTurn
        {
            Role = "assistant",
            Content = "[tool_use]",
            ToolCalls = [.. toolNames.Select(name => new ToolInvocation { ToolName = name, Arguments = "{}" })]
        });
}
