using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Approvals;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway.Approvals;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B1 invocation approval channel: a meta tool step that needs a human decision is parked
/// (durable, deterministic id) instead of being auto-denied, an approved grant lets exactly the
/// reviewed call run, and the channel stays opt-in.
/// </summary>
public sealed class MetaApprovalChannelTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(), "openclaw-meta-approval-tests", Guid.NewGuid().ToString("N"));

    public MetaApprovalChannelTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Disabled_channel_keeps_the_auto_deny_behaviour()
    {
        var executor = CreateExecutor(metaApprovals: null, out var tool);

        var result = await executor.ExecuteAsync(
            "gated_tool",
            """{"amount":10}""",
            "call-1",
            NewSession(),
            new TurnContext { SessionId = "session-1", ChannelId = "test" },
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken,
            metaApproval: new MetaApprovalContext("skill-a", "step-1", "caller-1"));

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.Null(result.ApprovalId);
        Assert.Contains("auto-denied", result.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(0, tool.Invocations);
    }

    [Fact]
    public async Task Gated_tool_step_is_parked_with_a_deterministic_pending_approval()
    {
        var store = CreateStore();
        var executor = CreateExecutor(store, out var tool);

        var first = await executor.ExecuteAsync(
            "gated_tool",
            """{"amount":10}""",
            "call-1",
            NewSession(),
            new TurnContext { SessionId = "session-1", ChannelId = "test" },
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken,
            metaApproval: new MetaApprovalContext("skill-a", "step-1", "caller-1"));

        Assert.Equal(ToolResultStatuses.Blocked, first.ResultStatus);
        Assert.NotNull(first.ApprovalId);
        Assert.Equal(0, tool.Invocations);

        var record = await store.TryGetAsync(first.ApprovalId!, TestContext.Current.CancellationToken);
        Assert.NotNull(record);
        Assert.Equal("session-1", record!.SessionId);
        Assert.Equal("caller-1", record.CallerId);
        Assert.Equal("skill-a", record.SkillName);
        Assert.Equal("step-1", record.StepId);
        Assert.Equal("gated_tool", record.ToolName);
        Assert.True(record.IsPending);

        // Re-entering the same step must not queue a second approval.
        var second = await executor.ExecuteAsync(
            "gated_tool",
            """{"amount":10}""",
            "call-2",
            NewSession(),
            new TurnContext { SessionId = "session-1", ChannelId = "test" },
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken,
            metaApproval: new MetaApprovalContext("skill-a", "step-1", "caller-1"));

        Assert.Equal(first.ApprovalId, second.ApprovalId);
        Assert.Single(await store.ListAsync("caller-1", null, pendingOnly: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Approved_grant_lets_the_reviewed_call_run_once()
    {
        var store = CreateStore();
        var executor = CreateExecutor(store, out var tool);
        var session = NewSession();
        var turnCtx = new TurnContext { SessionId = session.Id, ChannelId = "test" };
        var metaApproval = new MetaApprovalContext("skill-a", "step-1", "caller-1");

        var parked = await executor.ExecuteAsync(
            "gated_tool", """{"amount":10}""", "call-1", session, turnCtx,
            isStreaming: false, approvalCallback: null, ct: TestContext.Current.CancellationToken, metaApproval: metaApproval);

        var decision = await store.TryDecideAsync(
            parked.ApprovalId!, approved: true, "reviewer", "looks fine", TestContext.Current.CancellationToken);
        Assert.Equal(MetaApprovalDecideStatus.Recorded, decision);

        var resumed = await executor.ExecuteAsync(
            "gated_tool", """{"amount":10}""", "call-2", session, turnCtx,
            isStreaming: false, approvalCallback: null, ct: TestContext.Current.CancellationToken, metaApproval: metaApproval);

        Assert.Equal(ToolResultStatuses.Completed, resumed.ResultStatus);
        Assert.Null(resumed.ApprovalId);
        Assert.Equal(1, tool.Invocations);
        var grant = await store.TryGetAsync(parked.ApprovalId!, TestContext.Current.CancellationToken);
        Assert.NotNull(grant!.ConsumedAtUtc);
    }

    [Fact]
    public async Task Re_render_with_different_arguments_is_not_covered_by_the_grant()
    {
        var store = CreateStore();
        var executor = CreateExecutor(store, out var tool);
        var session = NewSession();
        var turnCtx = new TurnContext { SessionId = session.Id, ChannelId = "test" };
        var metaApproval = new MetaApprovalContext("skill-a", "step-1", "caller-1");

        var parked = await executor.ExecuteAsync(
            "gated_tool", """{"amount":10}""", "call-1", session, turnCtx,
            isStreaming: false, approvalCallback: null, ct: TestContext.Current.CancellationToken, metaApproval: metaApproval);
        await store.TryDecideAsync(parked.ApprovalId!, approved: true, "reviewer", null, TestContext.Current.CancellationToken);

        var changed = await executor.ExecuteAsync(
            "gated_tool", """{"amount":999999}""", "call-2", session, turnCtx,
            isStreaming: false, approvalCallback: null, ct: TestContext.Current.CancellationToken, metaApproval: metaApproval);

        Assert.Equal(ToolResultStatuses.Blocked, changed.ResultStatus);
        Assert.NotNull(changed.ApprovalId);
        Assert.NotEqual(parked.ApprovalId, changed.ApprovalId);
        Assert.Equal(0, tool.Invocations);
    }

    [Fact]
    public async Task Decided_approval_cannot_be_decided_again()
    {
        var store = CreateStore();
        var record = await store.GetOrCreateAsync(
            NewRequest("session-1", "caller-1", """{"amount":10}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            MetaApprovalDecideStatus.Recorded,
            await store.TryDecideAsync(record.ApprovalId, approved: false, "reviewer", "nope", TestContext.Current.CancellationToken));
        Assert.Equal(
            MetaApprovalDecideStatus.AlreadyDecided,
            await store.TryDecideAsync(record.ApprovalId, approved: true, "reviewer", null, TestContext.Current.CancellationToken));
        Assert.Equal(
            MetaApprovalDecideStatus.NotFound,
            await store.TryDecideAsync("map_doesnotexist", approved: true, "reviewer", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Grant_expires_so_a_stale_approval_cannot_be_reused()
    {
        var store = new MetaApprovalStore(
            new MetaApprovalOptions { Enabled = true, GrantTtl = TimeSpan.FromMilliseconds(1) },
            _root);
        var request = NewRequest("session-1", "caller-1", """{"amount":10}""");
        var record = await store.GetOrCreateAsync(request, TestContext.Current.CancellationToken);
        await store.TryDecideAsync(record.ApprovalId, approved: true, "reviewer", null, TestContext.Current.CancellationToken);

        await Task.Delay(20, TestContext.Current.CancellationToken);

        var grant = await store.FindActiveGrantAsync(
            "session-1", "gated_tool", record.ArgsFingerprint, TestContext.Current.CancellationToken);
        Assert.Null(grant);
    }

    [Fact]
    public async Task Pending_and_decided_approvals_survive_a_reopen()
    {
        var record = await CreateStore().GetOrCreateAsync(
            NewRequest("session-9", "caller-9", """{"amount":7}"""),
            TestContext.Current.CancellationToken);
        await CreateStore().TryDecideAsync(
            record.ApprovalId, approved: true, "reviewer", "ok", TestContext.Current.CancellationToken);

        using var reopened = CreateStore();
        var persisted = await reopened.TryGetAsync(record.ApprovalId, TestContext.Current.CancellationToken);

        Assert.NotNull(persisted);
        Assert.Equal(MetaApprovalDecisions.Approved, persisted!.Decision);
        Assert.Equal("reviewer", persisted.DecidedBy);
        Assert.NotNull(await reopened.FindActiveGrantAsync(
            "session-9", "gated_tool", persisted.ArgsFingerprint, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Approvals_are_listed_only_for_their_caller()
    {
        var store = CreateStore();
        await store.GetOrCreateAsync(NewRequest("session-1", "caller-1", """{"amount":1}"""), TestContext.Current.CancellationToken);
        await store.GetOrCreateAsync(NewRequest("session-2", "caller-2", """{"amount":2}"""), TestContext.Current.CancellationToken);

        Assert.Single(await store.ListAsync("caller-1", null, pendingOnly: true, TestContext.Current.CancellationToken));
        Assert.Single(await store.ListAsync("caller-2", "session-2", pendingOnly: true, TestContext.Current.CancellationToken));
        Assert.Empty(await store.ListAsync("caller-1", "session-2", pendingOnly: true, TestContext.Current.CancellationToken));
    }

    private MetaApprovalStore CreateStore()
        => new(
            new MetaApprovalOptions { Enabled = true },
            _root,
            NullLogger<MetaApprovalStore>.Instance);

    private static MetaApprovalRequest NewRequest(string sessionId, string callerId, string argsJson)
        => new(
            sessionId,
            callerId,
            "skill-a",
            "step-1",
            "gated_tool",
            argsJson,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("gated_tool|" + argsJson)))[..32],
            "review me");

    private static Session NewSession() => new() { Id = "session-1", ChannelId = "test", SenderId = "user-1" };

    private static OpenClawToolExecutor CreateExecutor(IMetaApprovalStore? metaApprovals, out GatedTool tool)
    {
        tool = new GatedTool();
        return new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 30,
            requireToolApproval: true,
            approvalRequiredTools: ["gated_tool"],
            hooks: [],
            logger: NullLogger.Instance,
            config: new GatewayConfig
            {
                Tooling = new ToolingConfig { RequireToolApproval = true, ApprovalRequiredTools = ["gated_tool"] }
            },
            metaApprovals: metaApprovals);
    }

    private sealed class GatedTool : ITool
    {
        public int Invocations { get; private set; }

        public string Name => "gated_tool";
        public string Description => "A tool that mutates money and therefore needs approval.";
        public string ParameterSchema => """{"type":"object","properties":{"amount":{"type":"integer"}}}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = argumentsJson;
            _ = ct;
            Invocations++;
            return new ValueTask<string>("ok");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
