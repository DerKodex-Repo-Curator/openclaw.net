using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Approvals;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Approvals;
using OpenClaw.Gateway.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B2 paused protocol at the Gateway: a run that stops on a human-gated step is reported as
/// <c>AwaitingInput</c> (HTTP 200) instead of <c>Completed</c>, a same-key POST without the
/// resume header is a pure query, and only <c>X-OpenClaw-Resume</c> re-enters the run.
/// </summary>
public sealed class MetaInvocationPauseProtocolTests
{
    [Fact]
    public async Task Paused_run_is_reported_as_awaiting_input_and_a_bare_repost_does_not_reenter_it()
    {
        using var harness = new Harness(pausedStepId: "collect-order-id");
        var session = await harness.CreateSessionAsync();
        var request = new MetaSkillInvocationRequest("triage-order", "incoming", session.Id);

        var first = await harness.Service.InvokeAsync(
            harness.CallerId, harness.CallerId, isAdmin: true, "key-1", request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, first.StatusCode);
        Assert.NotNull(first.Response);
        Assert.Equal(MetaInvocationStatus.AwaitingInput, first.Response!.Status);
        Assert.Null(first.Response.Error);
        Assert.NotNull(first.Response.Pause);
        Assert.Equal("user_input", first.Response.Pause!.Reason);
        Assert.Equal("collect-order-id", first.Response.Pause.StepId);
        Assert.Equal("Which order?", first.Response.Pause.Prompt);
        Assert.Null(first.Response.Pause.ApprovalId);
        Assert.Equal(1, harness.Runtime.MetaInvocations);

        // Same key, no resume header: a pure query, the run is not entered again.
        var query = await harness.Service.InvokeAsync(
            harness.CallerId, harness.CallerId, isAdmin: true, "key-1", request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, query.StatusCode);
        Assert.Equal(MetaInvocationStatus.AwaitingInput, query.Response!.Status);
        Assert.Equal(first.Response.InvocationId, query.Response.InvocationId);
        Assert.Equal(1, harness.Runtime.MetaInvocations);
    }

    [Fact]
    public async Task Resume_header_answers_the_pending_step_and_completes_the_same_ledger_row()
    {
        using var harness = new Harness(pausedStepId: "collect-order-id");
        var session = await harness.CreateSessionAsync();
        var request = new MetaSkillInvocationRequest("triage-order", "incoming", session.Id);

        var paused = await harness.Service.InvokeAsync(
            harness.CallerId, harness.CallerId, isAdmin: true, "key-2", request, TestContext.Current.CancellationToken);

        var resumed = await harness.Service.InvokeAsync(
            harness.CallerId,
            harness.CallerId,
            isAdmin: true,
            "key-2",
            request,
            TestContext.Current.CancellationToken,
            new MetaInvocationResumeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["collect-order-id"] = "ORDER-42"
            }));

        Assert.Equal(StatusCodes.Status200OK, resumed.StatusCode);
        Assert.Equal(MetaInvocationStatus.Completed, resumed.Response!.Status);
        Assert.Null(resumed.Response.Pause);
        Assert.Equal(paused.Response!.InvocationId, resumed.Response.InvocationId);
        Assert.Equal("ORDER-42", harness.Runtime.LastInput);
        Assert.Equal(2, harness.Runtime.MetaInvocations);
        // The resume continues the same logical invocation: no second ledger row.
        Assert.Equal(1, harness.LedgerRowCount());
    }

    [Fact]
    public async Task Resume_header_on_a_completed_invocation_is_ignored()
    {
        using var harness = new Harness(pausedStepId: null);
        var session = await harness.CreateSessionAsync();
        var request = new MetaSkillInvocationRequest("triage-order", "incoming", session.Id);

        var completed = await harness.Service.InvokeAsync(
            harness.CallerId, harness.CallerId, isAdmin: true, "key-3", request, TestContext.Current.CancellationToken);

        var replay = await harness.Service.InvokeAsync(
            harness.CallerId,
            harness.CallerId,
            isAdmin: true,
            "key-3",
            request,
            TestContext.Current.CancellationToken,
            new MetaInvocationResumeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["collect-order-id"] = "ORDER-99"
            }));

        Assert.Equal(StatusCodes.Status200OK, completed.StatusCode);
        Assert.Equal(MetaInvocationStatus.Completed, replay.Response!.Status);
        Assert.Equal(completed.Response!.InvocationId, replay.Response.InvocationId);
        Assert.Equal(1, harness.Runtime.MetaInvocations);
    }

    [Fact]
    public async Task Resume_payload_without_the_pending_step_keeps_the_run_paused()
    {
        using var harness = new Harness(pausedStepId: "collect-order-id", alwaysPause: true);
        var session = await harness.CreateSessionAsync();
        var request = new MetaSkillInvocationRequest("triage-order", "incoming", session.Id);

        await harness.Service.InvokeAsync(
            harness.CallerId, harness.CallerId, isAdmin: true, "key-4", request, TestContext.Current.CancellationToken);

        // An unknown step id must not be injected as the answer.
        var resumed = await harness.Service.InvokeAsync(
            harness.CallerId,
            harness.CallerId,
            isAdmin: true,
            "key-4",
            request,
            TestContext.Current.CancellationToken,
            new MetaInvocationResumeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["other-step"] = "nope"
            }));

        Assert.Equal(StatusCodes.Status200OK, resumed.StatusCode);
        Assert.Equal(MetaInvocationStatus.AwaitingInput, resumed.Response!.Status);
        Assert.Null(harness.Runtime.LastInput);
    }

    [Fact]
    public async Task Pending_approval_keeps_the_run_paused_without_reentering_it()
    {
        using var harness = new Harness(pausedStepId: "charge-card", approvalId: "approval-placeholder");
        var session = await harness.CreateSessionAsync();
        var request = new MetaSkillInvocationRequest("triage-order", "incoming", session.Id);
        var approval = await harness.CreateApprovalAsync(session.Id, "charge-card");

        var paused = await harness.Service.InvokeAsync(
            harness.CallerId, harness.CallerId, isAdmin: true, "key-5", request, TestContext.Current.CancellationToken);
        Assert.Equal(MetaInvocationStatus.AwaitingInput, paused.Response!.Status);
        Assert.Equal("tool_approval", paused.Response.Pause!.Reason);
        Assert.Equal(approval, paused.Response.Pause.ApprovalId);

        var resumed = await harness.Service.InvokeAsync(
            harness.CallerId,
            harness.CallerId,
            isAdmin: true,
            "key-5",
            request,
            TestContext.Current.CancellationToken,
            new MetaInvocationResumeRequest(new Dictionary<string, string>(StringComparer.Ordinal)));

        Assert.Equal(MetaInvocationStatus.AwaitingInput, resumed.Response!.Status);
        Assert.Equal(1, harness.Runtime.MetaInvocations);
    }

    [Fact]
    public async Task Denied_approval_finishes_the_invocation_with_approval_denied()
    {
        using var harness = new Harness(pausedStepId: "charge-card", approvalId: "approval-placeholder");
        var session = await harness.CreateSessionAsync();
        var request = new MetaSkillInvocationRequest("triage-order", "incoming", session.Id);
        var approval = await harness.CreateApprovalAsync(session.Id, "charge-card");
        await harness.Service.InvokeAsync(
            harness.CallerId, harness.CallerId, isAdmin: true, "key-6", request, TestContext.Current.CancellationToken);

        await harness.Approvals!.TryDecideAsync(approval, approved: false, harness.CallerId, "too risky", TestContext.Current.CancellationToken);

        var resumed = await harness.Service.InvokeAsync(
            harness.CallerId,
            harness.CallerId,
            isAdmin: true,
            "key-6",
            request,
            TestContext.Current.CancellationToken,
            new MetaInvocationResumeRequest(new Dictionary<string, string>(StringComparer.Ordinal)));

        Assert.Equal(MetaInvocationStatus.Completed, resumed.Response!.Status);
        Assert.Equal("approval.denied", resumed.Response.Error);
        Assert.Null(resumed.Response.Pause);
        // The run is never re-entered for a denied approval.
        Assert.Equal(1, harness.Runtime.MetaInvocations);
    }

    [Fact]
    public async Task Approved_decision_resumes_the_run_and_completes_it()
    {
        using var harness = new Harness(pausedStepId: "charge-card", approvalId: "approval-placeholder");
        var session = await harness.CreateSessionAsync();
        var request = new MetaSkillInvocationRequest("triage-order", "incoming", session.Id);
        var approval = await harness.CreateApprovalAsync(session.Id, "charge-card");
        await harness.Service.InvokeAsync(
            harness.CallerId, harness.CallerId, isAdmin: true, "key-7", request, TestContext.Current.CancellationToken);
        await harness.Approvals!.TryDecideAsync(approval, approved: true, harness.CallerId, "ok", TestContext.Current.CancellationToken);

        var resumed = await harness.Service.InvokeAsync(
            harness.CallerId,
            harness.CallerId,
            isAdmin: true,
            "key-7",
            request,
            TestContext.Current.CancellationToken,
            new MetaInvocationResumeRequest(new Dictionary<string, string>(StringComparer.Ordinal)));

        Assert.Equal(MetaInvocationStatus.Completed, resumed.Response!.Status);
        Assert.Null(resumed.Response.Error);
        Assert.Equal(2, harness.Runtime.MetaInvocations);
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly FileMemoryStore _memoryStore;
        private readonly SessionManager _sessionManager;

        public Harness(string? pausedStepId, bool alwaysPause = false, string? approvalId = null)
        {
            _root = Path.Join(Path.GetTempPath(), "openclaw-meta-pause-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _memoryStore = new FileMemoryStore(Path.Join(_root, "memory"));
            _sessionManager = new SessionManager(_memoryStore, new GatewayConfig(), NullLogger.Instance);
            Ledger = new MetaInvocationStore(Path.Join(_root, "ledger"), retentionDays: 30);
            Runtime = new PausingRuntime(pausedStepId, alwaysPause, approvalId);
            Approvals = approvalId is null
                ? null
                : new MetaApprovalStore(new MetaApprovalOptions { Enabled = true }, Path.Join(_root, "approvals"));
            Service = new MetaInvocationService(
                Ledger,
                _sessionManager,
                Runtime,
                NullLogger<MetaInvocationService>.Instance,
                Approvals);
        }

        public string CallerId => "account-1";

        public MetaInvocationStore Ledger { get; }

        public PausingRuntime Runtime { get; }

        public MetaApprovalStore? Approvals { get; }

        public MetaInvocationService Service { get; }

        public async Task<Session> CreateSessionAsync()
            => await _sessionManager.GetOrCreateAsync(
                "test-channel", "sender-1", TestContext.Current.CancellationToken);

        public async Task<string> CreateApprovalAsync(string sessionId, string stepId)
        {
            var record = await Approvals!.GetOrCreateAsync(
                new MetaApprovalRequest(
                    sessionId,
                    CallerId,
                    "triage-order",
                    stepId,
                    "charge-card-tool",
                    "{}",
                    "fingerprint",
                    "charge the card"),
                TestContext.Current.CancellationToken);
            Runtime.PauseApprovalId = record.ApprovalId;
            return record.ApprovalId;
        }

        /// <summary>Number of rows in the durable ledger (one per idempotency key).</summary>
        public int LedgerRowCount()
        {
            var path = Path.Join(_root, "ledger", "meta-invocations.json");
            if (!File.Exists(path))
                return 0;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetArrayLength();
        }

        public void Dispose()
        {
            Ledger.Dispose();
            _memoryStore.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Runtime stub that reproduces the real pause contract: it writes the durable checkpoint
    /// when a step needs a human and clears it when the answer finally arrives.
    /// </summary>
    private sealed class PausingRuntime(string? pausedStepId, bool alwaysPause, string? approvalId = null) : IAgentRuntime
    {
        public int MetaInvocations { get; private set; }

        public string? PauseApprovalId { get; set; }

        public string? LastInput { get; private set; }

        public CircuitState CircuitBreakerState => CircuitState.Closed;

        public IReadOnlyList<string> LoadedSkillNames => [];

        public IReadOnlyList<SkillDefinition> LoadedSkills => [];

        public Task<string> RunAsync(
            Session session,
            string userMessage,
            CancellationToken ct,
            ToolApprovalCallback? approvalCallback = null,
            JsonElement? responseSchema = null,
            string? correlationId = null,
            McpCallerCredentialContext? callerCredentialContext = null)
            => Task.FromResult("run");

        public Task<AgentTurnResult> RunTurnAsync(
            Session session,
            string userMessage,
            CancellationToken ct,
            ToolApprovalCallback? approvalCallback = null,
            JsonElement? responseSchema = null,
            string? correlationId = null,
            McpCallerCredentialContext? callerCredentialContext = null)
            => Task.FromResult(AgentTurnResult.Completed("run"));

        public Task<string> InvokeMetaSkillAsync(
            Session session,
            string skillName,
            string? input,
            CancellationToken cancellationToken = default)
        {
            MetaInvocations++;
            LastInput = input;

            // A checkpoint means this call resumes a paused run instead of starting one.
            var resuming = session.MetaExecutionCheckpoint is not null;
            if (pausedStepId is not null && (!resuming || alwaysPause))
            {
                session.MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                {
                    SkillName = skillName,
                    PendingStepId = pausedStepId,
                    Prompt = "Which order?",
                    PendingStepIds = [pausedStepId],
                    Reason = approvalId is null ? "user_input" : "tool_approval",
                    ApprovalId = PauseApprovalId ?? approvalId
                };
                return Task.FromResult($"Meta step '{pausedStepId}' requires user input.");
            }

            session.MetaExecutionCheckpoint = null;
            return Task.FromResult("order triaged");
        }

        public Task<IReadOnlyList<string>> ReloadSkillsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
            Session session,
            string userMessage,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
            ToolApprovalCallback? approvalCallback = null,
            string? correlationId = null,
            McpCallerCredentialContext? callerCredentialContext = null)
        {
            _ = session;
            _ = userMessage;
            _ = ct;
            _ = approvalCallback;
            _ = correlationId;
            _ = callerCredentialContext;
            await Task.CompletedTask;
            yield break;
        }
    }
}
