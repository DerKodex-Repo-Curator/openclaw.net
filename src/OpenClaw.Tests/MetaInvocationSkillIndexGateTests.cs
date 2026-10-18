using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B4 meta-invocation gate (design §7.3, T-B4-7/8). An index that never published must produce a
/// deliberate 503 before anything durable is written - not a random "skill not found" later.
/// </summary>
public sealed class MetaInvocationSkillIndexGateTests
{
    [Fact]
    public async Task T_B4_7_An_unready_index_answers_503_without_creating_an_invocation()
    {
        using var harness = new Harness(failClosed: true);

        var result = await harness.InvokeAsync();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("skills.not_ready", result.Error);
        Assert.Null(result.Response);

        // The gate runs before the session is even loaded, so nothing reached the ledger.
        Assert.Equal(0, harness.LedgerRowCount());
    }

    [Fact]
    public async Task T_B4_8_The_compatibility_switch_lets_the_call_through()
    {
        using var harness = new Harness(failClosed: false);

        var result = await harness.InvokeAsync();

        // Not 503: the call passed the gate and failed later on the unknown session, which is the
        // pre-B4 behaviour this switch exists to preserve.
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Equal("Session not found.", result.Error);
    }

    [Fact]
    public async Task A_ready_index_does_not_gate_anything()
    {
        using var harness = new Harness(failClosed: true, ready: true);

        var result = await harness.InvokeAsync();

        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly FileMemoryStore _memoryStore;
        private readonly MetaInvocationStore _ledger;

        public Harness(bool failClosed, bool ready = false)
        {
            _root = Path.Join(Path.GetTempPath(), "openclaw-skill-index-gate", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _memoryStore = new FileMemoryStore(Path.Join(_root, "memory"));
            _ledger = new MetaInvocationStore(Path.Join(_root, "ledger"), retentionDays: 30);

            var skillRoot = Path.Join(_root, "skills");
            Directory.CreateDirectory(Path.Join(skillRoot, "alpha"));
            File.WriteAllText(
                Path.Join(skillRoot, "alpha", "SKILL.md"),
                """
                ---
                name: alpha
                description: alpha skill
                ---

                Body.
                """);

            var compiler = new SkillIndexCompiler(
                new SkillsConfig
                {
                    Load = new SkillLoadConfig
                    {
                        ExtraDirs = [skillRoot],
                        IncludeBundled = false,
                        IncludeManaged = false,
                        IncludeWorkspace = false
                    }
                },
                NullLogger<SkillIndexCompiler>.Instance);

            if (ready)
            {
                compiler
                    .CompileAsync(SkillIndexCompileTrigger.Startup, TestContext.Current.CancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }

            var sessionManager = new SessionManager(_memoryStore, new GatewayConfig(), NullLogger.Instance);
            Service = new MetaInvocationService(
                _ledger,
                sessionManager,
                new NoopRuntime(),
                NullLogger<MetaInvocationService>.Instance,
                approvals: null,
                skillIndexCompiler: compiler,
                skillsConfig: new SkillsConfig { FailClosedOnUnreadyIndex = failClosed });
        }

        public MetaInvocationService Service { get; }

        public Task<MetaInvocationExecutionResult> InvokeAsync()
            => Service.InvokeAsync(
                callerId: "account-1",
                accountId: "account-1",
                isAdmin: false,
                idempotencyKey: "gate-" + Guid.NewGuid().ToString("N"),
                request: new MetaSkillInvocationRequest("alpha", "hello", "session-that-does-not-exist"),
                cancellationToken: TestContext.Current.CancellationToken);

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
            _ledger.Dispose();
            _memoryStore.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The gate must fail before the runtime is ever consulted; this runtime proves it.</summary>
    private sealed class NoopRuntime : IAgentRuntime
    {
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
            => throw new InvalidOperationException("The skill index gate must not reach the runtime.");

        public Task<AgentTurnResult> RunTurnAsync(
            Session session,
            string userMessage,
            CancellationToken ct,
            ToolApprovalCallback? approvalCallback = null,
            JsonElement? responseSchema = null,
            string? correlationId = null,
            McpCallerCredentialContext? callerCredentialContext = null)
            => throw new InvalidOperationException("The skill index gate must not reach the runtime.");

        public Task<string> InvokeMetaSkillAsync(
            Session session,
            string skillName,
            string? input,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The skill index gate must not reach the runtime.");

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
            await Task.CompletedTask;
            yield break;
        }
    }
}
