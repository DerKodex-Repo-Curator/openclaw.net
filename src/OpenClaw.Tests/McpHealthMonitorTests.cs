using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B5 state machine and probe loop (design §8.1/§8.2, T-B5-1..9).
/// </summary>
/// <remarks>
/// The probe is faked at <see cref="IMcpHealthProbe"/>, so these tests pin the thresholds, the
/// transition events and the visibility rules without a live MCP server. The registry is real: it
/// supplies the server list, which is what makes T-B5-8 ("a server appears mid-flight") meaningful.
/// </remarks>
public sealed class McpHealthMonitorTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task T_B5_1_A_successful_probe_reports_healthy_with_a_latency()
    {
        var (store, monitor, probe) = CreateHarness("alpha");
        probe.Enqueue(McpProbeOutcome.Ok(12, ["read", "write"]));

        await monitor.RunRoundAsync(Ct);

        var health = store.Get("alpha");
        Assert.Equal(McpServerHealthState.Healthy, health.State);
        Assert.Equal(12, health.LatencyMs);
        Assert.Equal(2, health.ToolCount);
        Assert.Null(health.LastErrorCode);
        Assert.NotNull(health.LastSuccessAtUtc);
        Assert.True(store.IsAvailable("alpha"));
    }

    [Fact]
    public async Task T_B5_2_Two_failures_degrade_but_keep_the_tools_visible()
    {
        var (store, monitor, probe) = CreateHarness("alpha");
        var changed = new List<McpHealthTransition>();
        store.Changed += transition => changed.Add(transition);

        probe.Enqueue(McpProbeOutcome.Failed(5, McpProbeErrorCodes.Timeout));
        probe.Enqueue(McpProbeOutcome.Failed(5, McpProbeErrorCodes.Timeout));

        await monitor.RunRoundAsync(Ct);
        await monitor.RunRoundAsync(Ct);

        var health = store.Get("alpha");
        Assert.Equal(McpServerHealthState.Degraded, health.State);
        Assert.Equal(2, health.ConsecutiveFailures);
        Assert.Equal(McpProbeErrorCodes.Timeout, health.LastErrorCode);
        Assert.Null(health.LatencyMs);

        // One event for the single state change, not one per probe.
        Assert.Single(changed);
        Assert.True(changed[0].IsDeterioration);

        // Degraded is visible on purpose: hiding a tool the model already planned around is worse
        // than letting the call fail once.
        var executor = CreateExecutor(store, new FakeMcpTool("alpha_read", "alpha"));
        Assert.Contains(executor.GetToolDeclarations(NewSession()), item => item.Name == "alpha_read");
    }

    [Fact]
    public async Task T_B5_3_Five_failures_take_the_server_down_and_refuse_calls()
    {
        var (store, monitor, probe) = CreateHarness("alpha");
        for (var i = 0; i < 5; i++)
            probe.Enqueue(McpProbeOutcome.Failed(5, McpProbeErrorCodes.Transport));

        for (var i = 0; i < 5; i++)
            await monitor.RunRoundAsync(Ct);

        Assert.Equal(McpServerHealthState.Down, store.Get("alpha").State);
        Assert.False(store.IsAvailable("alpha"));

        var tool = new FakeMcpTool("alpha_read", "alpha");
        var executor = CreateExecutor(store, tool);

        // Hidden from the model...
        Assert.DoesNotContain(executor.GetToolDeclarations(NewSession()), item => item.Name == "alpha_read");

        // ...and refused even if a turn still holds the old declaration.
        var result = await executor.ExecuteAsync(
            "alpha_read",
            "{}",
            callId: null,
            NewSession(),
            NewTurn(),
            isStreaming: false,
            approvalCallback: null,
            Ct);

        Assert.Equal(ToolFailureCodes.McpServerUnavailable, result.FailureCode);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.False(tool.Called);
    }

    [Fact]
    public async Task T_B5_4_Recovery_needs_three_successes_and_brings_the_tools_back()
    {
        var (store, monitor, probe) = CreateHarness("alpha");
        for (var i = 0; i < 5; i++)
            probe.Enqueue(McpProbeOutcome.Failed(5, McpProbeErrorCodes.Timeout));
        for (var i = 0; i < 5; i++)
            await monitor.RunRoundAsync(Ct);
        Assert.Equal(McpServerHealthState.Down, store.Get("alpha").State);

        var changed = new List<McpHealthTransition>();
        store.Changed += transition => changed.Add(transition);

        for (var i = 0; i < 3; i++)
            probe.Enqueue(McpProbeOutcome.Ok(8, ["read"]));
        for (var i = 0; i < 3; i++)
            await monitor.RunRoundAsync(Ct);

        Assert.Equal(McpServerHealthState.Healthy, store.Get("alpha").State);
        Assert.Single(changed);
        Assert.True(changed[0].IsRecovery);

        var executor = CreateExecutor(store, new FakeMcpTool("alpha_read", "alpha"));
        Assert.Contains(executor.GetToolDeclarations(NewSession()), item => item.Name == "alpha_read");
    }

    [Fact]
    public async Task T_B5_5_A_changed_tool_inventory_degrades_and_triggers_a_reindex()
    {
        var (store, monitor, probe) = CreateHarness("alpha");
        probe.Enqueue(McpProbeOutcome.Ok(5, ["read", "write"]));
        await monitor.RunRoundAsync(Ct);
        Assert.Equal(McpServerHealthState.Healthy, store.Get("alpha").State);

        probe.Enqueue(McpProbeOutcome.Ok(5, ["read", "write", "delete"]));
        await monitor.RunRoundAsync(Ct);

        var health = store.Get("alpha");
        Assert.Equal(McpServerHealthState.Degraded, health.State);
        Assert.Equal(McpProbeErrorCodes.ToolsChanged, health.LastErrorCode);
        Assert.Equal(3, health.ToolCount);

        // The registry has no live client here, so the delta is empty; what is asserted is that the
        // monitor asked, which is the half of the contract that lives in the loop.
        Assert.Equal(1, monitor.ReindexAttempts);
    }

    [Fact]
    public async Task T_B5_6_A_slow_probe_does_not_block_a_tool_call()
    {
        var (store, monitor, probe) = CreateHarness("alpha");
        probe.Enqueue(McpProbeOutcome.Ok(5, ["read"]));
        await monitor.RunRoundAsync(Ct);

        var tool = new FakeMcpTool("alpha_read", "alpha");
        var executor = CreateExecutor(store, tool);

        // A probe that takes most of its timeout budget, running while the call is made.
        probe.EnqueueDelayed(McpProbeOutcome.Ok(5, ["read"]), TimeSpan.FromMilliseconds(600));
        var round = monitor.RunRoundAsync(Ct);

        var stopwatch = Stopwatch.StartNew();
        var result = await executor.ExecuteAsync(
            "alpha_read", "{}", callId: null, NewSession(), NewTurn(),
            isStreaming: false, approvalCallback: null, Ct);
        stopwatch.Stop();

        await round;

        Assert.Equal("ok", result.ResultText);
        Assert.True(stopwatch.ElapsedMilliseconds < 300, $"tool call took {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task T_B5_7_One_broken_server_does_not_affect_the_healthy_one()
    {
        var (store, monitor, probe) = CreateHarness("alpha", "beta");
        probe.Enqueue(McpProbeOutcome.Ok(5, ["read"]));
        probe.Enqueue(McpProbeOutcome.Failed(5, McpProbeErrorCodes.Timeout));
        await monitor.RunRoundAsync(Ct);

        for (var i = 0; i < 4; i++)
        {
            probe.Enqueue(McpProbeOutcome.Ok(5, ["read"]));
            probe.Enqueue(McpProbeOutcome.Failed(5, McpProbeErrorCodes.Timeout));
            await monitor.RunRoundAsync(Ct);
        }

        Assert.Equal(McpServerHealthState.Down, store.Get("beta").State);
        Assert.Equal(McpServerHealthState.Healthy, store.Get("alpha").State);

        var executor = CreateExecutor(
            store,
            new FakeMcpTool("alpha_read", "alpha"),
            new FakeMcpTool("beta_read", "beta"));

        var names = executor.GetToolDeclarations(NewSession()).Select(item => item.Name).ToArray();
        Assert.Contains("alpha_read", names);
        Assert.DoesNotContain("beta_read", names);
    }

    [Fact]
    public async Task T_B5_8_A_server_added_after_startup_is_probed_in_the_next_round()
    {
        var (store, monitor, probe, config) = CreateHarnessWithConfig("alpha");
        probe.Enqueue(McpProbeOutcome.Ok(5, ["read"]));
        await monitor.RunRoundAsync(Ct);

        config.Servers["gamma"] = new McpServerConfig { Name = "gamma" };
        probe.Enqueue(McpProbeOutcome.Ok(6, ["read"]));
        probe.Enqueue(McpProbeOutcome.Ok(7, ["read"]));
        await monitor.RunRoundAsync(Ct);

        Assert.Equal(McpServerHealthState.Healthy, store.Get("alpha").State);
        Assert.Equal(McpServerHealthState.Healthy, store.Get("gamma").State);
    }

    [Fact]
    public async Task T_B5_9_Stopping_the_loop_completes_quickly()
    {
        var (_, monitor, probe) = CreateHarness("alpha");
        probe.Enqueue(McpProbeOutcome.Ok(5, ["read"]));

        await monitor.StartAsync(Ct);

        var started = Stopwatch.StartNew();
        while (monitor.CompletedRounds < 1 && started.ElapsedMilliseconds < 5000)
            await Task.Delay(10, Ct);

        Assert.True(monitor.CompletedRounds >= 1, "the loop never completed a round");

        // The next tick is 30s away; StopAsync has to cancel it rather than wait for it, and the
        // 5s cap has to hold even if a probe is mid-flight.
        var stopwatch = Stopwatch.StartNew();
        await monitor.StopAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"stop took {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task T_B5_1b_A_server_that_never_connected_degrades_with_a_transport_error()
    {
        var (store, monitor, probe) = CreateHarness("alpha");
        probe.Enqueue(McpProbeOutcome.Failed(0, McpProbeErrorCodes.Transport, "server has no client"));
        probe.Enqueue(McpProbeOutcome.Failed(0, McpProbeErrorCodes.Transport, "server has no client"));

        await monitor.RunRoundAsync(Ct);
        await monitor.RunRoundAsync(Ct);

        var health = store.Get("alpha");
        Assert.Equal(McpServerHealthState.Degraded, health.State);
        Assert.Equal(McpProbeErrorCodes.Transport, health.LastErrorCode);
        Assert.Null(health.LastSuccessAtUtc);
    }

    [Fact]
    public async Task T_B5_2b_An_unknown_error_code_is_clamped_to_server_error()
    {
        var store = new McpServerHealthStore(new McpHealthConfig { FailureThreshold = 1 });

        store.RecordFailure("alpha", "socket refused by 10.0.0.1:9999");

        Assert.Equal(McpProbeErrorCodes.ServerError, store.Get("alpha").LastErrorCode);
    }

    [Fact]
    public void T_B5_9b_Thresholds_are_clamped_so_a_zero_cannot_disable_the_machine()
    {
        var config = new McpHealthConfig
        {
            IntervalSeconds = 0,
            ProbeTimeoutSeconds = 0,
            FailureThreshold = 0,
            DownThreshold = 1,
            RecoveryThreshold = 0
        }.Normalize();

        Assert.True(config.IntervalSeconds >= 1);
        Assert.True(config.ProbeTimeoutSeconds >= 1);
        Assert.True(config.FailureThreshold >= 1);
        Assert.True(config.DownThreshold >= config.FailureThreshold);
        Assert.True(config.RecoveryThreshold >= 1);
    }

    private static (McpServerHealthStore Store, McpServerHealthMonitor Monitor, FakeProbe Probe) CreateHarness(
        params string[] serverIds)
    {
        var (store, monitor, probe, _) = CreateHarnessWithConfig(serverIds);
        return (store, monitor, probe);
    }

    private static (McpServerHealthStore Store, McpServerHealthMonitor Monitor, FakeProbe Probe, McpPluginsConfig Config)
        CreateHarnessWithConfig(params string[] serverIds)
    {
        var config = new McpPluginsConfig { Enabled = true };
        foreach (var serverId in serverIds)
            config.Servers[serverId] = new McpServerConfig { Name = serverId };

        var registry = new McpServerToolRegistry(config, NullLogger<McpServerToolRegistry>.Instance);
        var store = new McpServerHealthStore(config.Health);
        var probe = new FakeProbe();
        var monitor = new McpServerHealthMonitor(registry, store, probe, NullLogger<McpServerHealthMonitor>.Instance);
        return (store, monitor, probe, config);
    }

    private static OpenClawToolExecutor CreateExecutor(McpServerHealthStore store, params ITool[] tools)
        => new(
            tools,
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            metrics: new RuntimeMetrics(),
            logger: NullLogger.Instance,
            mcpHealth: store);

    private static Session NewSession() => new()
    {
        Id = "sess-mcp-health",
        ChannelId = "websocket",
        SenderId = "user1"
    };

    private static TurnContext NewTurn() => new()
    {
        SessionId = "sess-mcp-health",
        ChannelId = "websocket"
    };

    /// <summary>A tool that belongs to a server and records whether it was reached.</summary>
    private sealed class FakeMcpTool(string name, string serverId) : ITool, IMcpServerBoundTool
    {
        public string Name => name;
        public string Description => name;
        public string ParameterSchema => """{"type":"object","properties":{}}""";
        public string McpServerId => serverId;

        public bool Called { get; private set; }

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            Called = true;
            return ValueTask.FromResult("ok");
        }
    }

    /// <summary>Queued probe results, in call order.</summary>
    private sealed class FakeProbe : IMcpHealthProbe
    {
        private readonly Queue<(McpProbeOutcome Outcome, TimeSpan Delay)> _queue = new();

        public void Enqueue(McpProbeOutcome outcome) => _queue.Enqueue((outcome, TimeSpan.Zero));

        public void EnqueueDelayed(McpProbeOutcome outcome, TimeSpan delay) => _queue.Enqueue((outcome, delay));

        public async Task<McpProbeOutcome> ProbeAsync(McpProbeTarget target, TimeSpan timeout, CancellationToken ct)
        {
            if (!_queue.TryDequeue(out var item))
                return McpProbeOutcome.Ok(1, []);

            if (item.Delay > TimeSpan.Zero)
                await Task.Delay(item.Delay, ct);

            return item.Outcome;
        }
    }
}
