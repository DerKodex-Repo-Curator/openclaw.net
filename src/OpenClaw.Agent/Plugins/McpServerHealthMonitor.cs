using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Plugins;

/// <summary>Result of one probe of one MCP server.</summary>
public sealed record McpProbeOutcome(
    bool Success,
    long LatencyMs,
    IReadOnlyList<string> ToolNames,
    string? ErrorCode,
    string? ErrorDetail)
{
    public static McpProbeOutcome Ok(long latencyMs, IReadOnlyList<string> toolNames)
        => new(true, latencyMs, toolNames, null, null);

    public static McpProbeOutcome Failed(long latencyMs, string errorCode, string? detail = null)
        => new(false, latencyMs, [], errorCode, detail);
}

/// <summary>
/// How the monitor talks to a server. Separated so the state machine and the thresholds can be
/// tested without a live MCP server, and so a host can swap in a cheaper check.
/// </summary>
public interface IMcpHealthProbe
{
    Task<McpProbeOutcome> ProbeAsync(McpProbeTarget target, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// <c>tools/list</c> probe: the lightest request that also reveals capability changes.
/// </summary>
public sealed class McpClientHealthProbe : IMcpHealthProbe
{
    public async Task<McpProbeOutcome> ProbeAsync(McpProbeTarget target, TimeSpan timeout, CancellationToken ct)
    {
        if (target.Client is null)
        {
            // It never connected, so there is nothing to talk to. Reporting a transport failure keeps
            // the server visible in /mcp-health instead of silently disappearing from it.
            return McpProbeOutcome.Failed(0, McpProbeErrorCodes.Transport, "server has no client");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var names = new List<string>();
            var parameters = new ListToolsRequestParams();
            do
            {
                var page = await target.Client.ListToolsAsync(parameters, cancellationToken: ct).ConfigureAwait(false);
                foreach (var tool in page.Tools)
                {
                    if (!string.IsNullOrWhiteSpace(tool.Name))
                        names.Add(tool.Name);
                }

                parameters = new ListToolsRequestParams
                {
                    Cursor = string.IsNullOrEmpty(page.NextCursor) ? null : page.NextCursor
                };
            }
            while (parameters.Cursor is not null);

            return McpProbeOutcome.Ok(stopwatch.ElapsedMilliseconds, names);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return McpProbeOutcome.Failed(stopwatch.ElapsedMilliseconds, McpProbeErrorCodes.Timeout);
        }
        catch (Exception ex)
        {
            return McpProbeOutcome.Failed(stopwatch.ElapsedMilliseconds, Classify(ex), ex.GetType().Name);
        }
    }

    /// <summary>
    /// Maps an exception onto the closed error-code set. The raw text never leaves the process, so
    /// an operator cannot accidentally read a token out of an error message.
    /// </summary>
    internal static string Classify(Exception ex) => ex switch
    {
        TimeoutException => McpProbeErrorCodes.Timeout,
        OperationCanceledException => McpProbeErrorCodes.Timeout,
        UnauthorizedAccessException => McpProbeErrorCodes.Unauthorized,
        JsonException => McpProbeErrorCodes.Protocol,
        McpProtocolException => McpProbeErrorCodes.Protocol,
        McpException => McpProbeErrorCodes.ServerError,
        HttpRequestException http => ClassifyHttp(http),
        SocketException => McpProbeErrorCodes.Transport,
        IOException => McpProbeErrorCodes.Transport,
        _ => McpProbeErrorCodes.ServerError
    };

    private static string ClassifyHttp(HttpRequestException ex)
        => ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? McpProbeErrorCodes.Unauthorized
            : McpProbeErrorCodes.Transport;
}

/// <summary>
/// Probes every configured MCP server on an interval and turns the results into health states.
/// </summary>
/// <remarks>
/// <para>
/// Probes are serial and run on their own cancellation chain, so a slow server (up to
/// <see cref="McpHealthConfig.ProbeTimeoutSeconds"/>) can never delay a tool call. A probe failure
/// is recorded, never thrown.
/// </para>
/// <para>
/// The loop only acts on state <em>changes</em>: logging or emitting an event for every successful
/// round would bury the one line that matters.
/// </para>
/// </remarks>
public sealed class McpServerHealthMonitor : BackgroundService
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly McpServerToolRegistry _registry;
    private readonly McpServerHealthStore _store;
    private readonly IMcpHealthProbe _probe;
    private readonly ILogger _logger;

    public McpServerHealthMonitor(
        McpServerToolRegistry registry,
        McpServerHealthStore store,
        IMcpHealthProbe probe,
        ILogger logger)
    {
        _registry = registry;
        _store = store;
        _probe = probe;
        _logger = logger;
    }

    /// <summary>
    /// Applies a tool-inventory change to the live tool table. Set by the host once the agent
    /// runtime exists (the runtime is built <em>from</em> this registry, so the monitor cannot take
    /// it as a dependency). Left null in tests, which assert on the registry alone.
    /// </summary>
    public Func<McpProbeTarget, McpWorkspaceReloadResult, CancellationToken, Task>? ToolsChanged { get; set; }

    /// <summary>Number of completed rounds. Exposed for diagnostics and tests.</summary>
    public long CompletedRounds => Interlocked.Read(ref _rounds);

    /// <summary>
    /// Number of tool-inventory reindexes attempted. A reindex that finds nothing to change still
    /// counts: the interesting signal is "a probe saw a different inventory", not the delta size.
    /// </summary>
    public long ReindexAttempts => Interlocked.Read(ref _reindexAttempts);

    private long _rounds;
    private long _reindexAttempts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_store.Config.Enabled)
        {
            // Monitoring off: still register the configured servers so /mcp-health shows them as
            // unknown rather than pretending they do not exist.
            await TrackConfiguredServersAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("MCP health monitoring is disabled by configuration.");
            return;
        }

        _logger.LogInformation(
            "MCP health monitoring started (interval={Interval}s, probe timeout={Timeout}s, degraded at {Failure}, down at {Down}, recovery at {Recovery}).",
            _store.Config.IntervalSeconds,
            _store.Config.ProbeTimeoutSeconds,
            _store.Config.FailureThreshold,
            _store.Config.DownThreshold,
            _store.Config.RecoveryThreshold);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_store.Config.IntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunRoundAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One pass over every server, serially. Public so a host or a test can force a round instead of
    /// waiting for the interval.
    /// </summary>
    public async Task RunRoundAsync(CancellationToken ct)
    {
        IReadOnlyList<McpProbeTarget> targets;
        try
        {
            targets = await _registry.GetProbeTargetsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MCP health: could not enumerate servers for this round.");
            return;
        }

        ForgetRemovedServers(targets);

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            // A server that appears mid-flight (workspace reload) starts as unknown and gets a
            // probed state in this same round (T-B5-8).
            _store.Track(target.ServerId);

            try
            {
                await ProbeOneAsync(target, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A probe is diagnostic work; it must never take the loop down.
                _logger.LogDebug(ex, "MCP health: probe of '{ServerId}' failed unexpectedly.", target.ServerId);
            }
        }

        Interlocked.Increment(ref _rounds);
    }

    private async Task ProbeOneAsync(McpProbeTarget target, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(_store.Config.ProbeTimeoutSeconds);

        // Isolated chain: cancelling a probe cannot cancel the turn that happens to be running, and
        // a tool call cannot cancel the probe.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(timeout);

        var outcome = await _probe.ProbeAsync(target, timeout, probeCts.Token).ConfigureAwait(false);

        Telemetry.McpProbeDuration.Record(
            outcome.LatencyMs,
            new KeyValuePair<string, object?>("server", target.ServerId));

        if (!outcome.Success)
        {
            Telemetry.McpProbeFailures.Add(
                1,
                new KeyValuePair<string, object?>("server", target.ServerId),
                new KeyValuePair<string, object?>("reason", outcome.ErrorCode ?? McpProbeErrorCodes.ServerError));
        }

        if (outcome.Success)
        {
            var transition = _store.RecordSuccess(target.ServerId, outcome.LatencyMs, outcome.ToolNames);
            LogTransition(target.ServerId, transition);

            if (transition.ToolsChanged)
                await ReindexAsync(target, ct).ConfigureAwait(false);

            return;
        }

        LogTransition(
            target.ServerId,
            _store.RecordFailure(target.ServerId, outcome.ErrorCode ?? McpProbeErrorCodes.ServerError));
    }

    private async Task ReindexAsync(McpProbeTarget target, CancellationToken ct)
    {
        Interlocked.Increment(ref _reindexAttempts);

        try
        {
            var delta = await _registry.RefreshServerToolsAsync(target.ServerId, ct).ConfigureAwait(false);
            if (delta.AddedTools.Count == 0 && delta.RemovedToolNames.Count == 0)
                return;

            _logger.LogInformation(
                "MCP server '{ServerId}': tool inventory changed (+{Added} / -{Removed}); registry reindexed.",
                target.ServerId,
                delta.AddedTools.Count,
                delta.RemovedToolNames.Count);

            if (ToolsChanged is { } onToolsChanged)
                await onToolsChanged(target, delta, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP server '{ServerId}': tool reindex failed.", target.ServerId);
        }
    }

    private void LogTransition(string serverId, McpHealthTransition transition)
    {
        if (!transition.StateChanged)
            return;

        switch (transition.Health.State)
        {
            case McpServerHealthState.Degraded:
                _logger.LogWarning(
                    "MCP server '{ServerId}' degraded after {Failures} consecutive failure(s) ({Code}). Tools stay visible.",
                    serverId,
                    transition.Health.ConsecutiveFailures,
                    transition.Health.LastErrorCode);
                break;

            case McpServerHealthState.Down:
                _logger.LogError(
                    "MCP server '{ServerId}' is down after {Failures} consecutive failure(s) ({Code}). Calls are refused{Visibility}.",
                    serverId,
                    transition.Health.ConsecutiveFailures,
                    transition.Health.LastErrorCode,
                    _store.Config.HideUnhealthyTools ? " and tools are hidden" : string.Empty);
                break;

            case McpServerHealthState.Healthy:
                _logger.LogInformation(
                    "MCP server '{ServerId}' recovered after {Successes} consecutive success(es) from {Previous}.",
                    serverId,
                    transition.Health.ConsecutiveSuccesses,
                    McpServerHealthStates.ToWireValue(transition.PreviousState));
                break;
        }
    }

    private void ForgetRemovedServers(IReadOnlyList<McpProbeTarget> targets)
    {
        var known = targets.Select(static t => t.ServerId).ToHashSet(StringComparer.Ordinal);
        foreach (var health in _store.Snapshot())
        {
            if (!known.Contains(health.ServerId))
                _store.Remove(health.ServerId);
        }
    }

    private async Task TrackConfiguredServersAsync(CancellationToken ct)
    {
        try
        {
            foreach (var target in await _registry.GetProbeTargetsAsync(ct).ConfigureAwait(false))
                _store.Track(target.ServerId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MCP health: could not enumerate servers.");
        }
    }

    /// <summary>
    /// Stops the loop and never blocks shutdown for longer than <see cref="StopTimeout"/>: a hung
    /// MCP client must not keep the gateway from exiting (T-B5-9).
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stopCts.CancelAfter(StopTimeout);

        try
        {
            await base.StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("MCP health monitor did not stop within {Seconds}s; abandoning the current probe.", StopTimeout.TotalSeconds);
        }
    }
}
