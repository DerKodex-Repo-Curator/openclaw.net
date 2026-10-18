using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenClaw.Core.Observability;

/// <summary>
/// Provides centralized OpenTelemetry instances (ActivitySource, Meters) for the OpenClaw application.
/// </summary>
public static class Telemetry
{
    public const string ServiceName = "OpenClaw.Gateway";
    public const string Version = "1.0.0";

    /// <summary>
    /// The primary ActivitySource for distributed tracing within OpenClaw.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ServiceName, Version);

    /// <summary>
    /// The primary Meter for OpenClaw metrics. Registered with the OTLP exporter via
    /// GatewayTelemetryExtensions.AddGatewayTelemetry (meter name "OpenClaw.Gateway").
    /// </summary>
    public static readonly Meter Meter = new(ServiceName, Version);

    /// <summary>
    /// Histogram recording the duration (ms) of each tool execution, tagged by tool name and success.
    /// </summary>
    public static readonly Histogram<double> ToolExecutionDuration =
        Meter.CreateHistogram<double>(
            "openclaw.tool.execution.duration",
            unit: "ms",
            description: "Duration of tool executions in milliseconds");

    /// <summary>
    /// Counter incremented when a request is blocked by rate limiting, tagged by actor type and policy.
    /// </summary>
    public static readonly Counter<long> RateLimitExceeded =
        Meter.CreateCounter<long>(
            "openclaw.ratelimit.exceeded",
            description: "Number of requests blocked by rate limiting");

    /// <summary>
    /// Registers an observable gauge that reports the current pending approval queue depth.
    /// Call once during startup after the ToolApprovalService is constructed.
    /// </summary>
    public static void RegisterApprovalQueueGauge(Func<int> observeFunc)
    {
        Meter.CreateObservableGauge(
            "openclaw.approval.queue.depth",
            observeFunc,
            description: "Number of pending tool approval requests");
    }

    /// <summary>Duration of one MCP health probe, tagged by server id (design §8.3).</summary>
    public static readonly Histogram<double> McpProbeDuration =
        Meter.CreateHistogram<double>(
            "openclaw.mcp.probe.duration",
            unit: "ms",
            description: "Duration of MCP health probes in milliseconds");

    /// <summary>Failed MCP probes, tagged by server id and the closed reason code.</summary>
    public static readonly Counter<long> McpProbeFailures =
        Meter.CreateCounter<long>(
            "openclaw.mcp.probe.failures",
            description: "Number of failed MCP health probes, by server and reason");

    /// <summary>
    /// Reports each MCP server's state as 0=<c>unknown</c>, 1=<c>healthy</c>, 2=<c>degraded</c>,
    /// 3=<c>down</c>, tagged by server id. Same shape as <see cref="RegisterApprovalQueueGauge"/>:
    /// registered once during startup with a snapshot accessor.
    /// </summary>
    public static void RegisterMcpHealthGauge(Func<IReadOnlyList<Plugins.McpServerHealth>> snapshot)
    {
        Meter.CreateObservableGauge(
            "openclaw.mcp.health.state",
            () => snapshot().Select(health => new Measurement<long>(
                Plugins.McpServerHealthStates.ToMetricValue(health.State),
                new KeyValuePair<string, object?>("server", health.ServerId))),
            description: "MCP server health (0 unknown, 1 healthy, 2 degraded, 3 down)");
    }
}
