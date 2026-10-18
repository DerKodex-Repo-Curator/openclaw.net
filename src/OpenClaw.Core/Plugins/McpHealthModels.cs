namespace OpenClaw.Core.Plugins;

/// <summary>
/// Health of one MCP server, as seen by the probe loop.
/// </summary>
public enum McpServerHealthState
{
    /// <summary>Configured but never probed yet (startup, or a server that was just reloaded in).</summary>
    Unknown = 0,

    /// <summary>Last probe succeeded and the tool inventory matched what is registered.</summary>
    Healthy = 1,

    /// <summary>Failing, but not enough times to hide its tools. Tools stay visible.</summary>
    Degraded = 2,

    /// <summary>Failing past the down threshold. Tools are hidden and calls are refused.</summary>
    Down = 3
}

/// <summary>Wire names for <see cref="McpServerHealthState"/>.</summary>
public static class McpServerHealthStates
{
    public const string Unknown = "unknown";
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Down = "down";

    public static string ToWireValue(McpServerHealthState state) => state switch
    {
        McpServerHealthState.Healthy => Healthy,
        McpServerHealthState.Degraded => Degraded,
        McpServerHealthState.Down => Down,
        _ => Unknown
    };

    public static McpServerHealthState Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Healthy => McpServerHealthState.Healthy,
        Degraded => McpServerHealthState.Degraded,
        Down => McpServerHealthState.Down,
        _ => McpServerHealthState.Unknown
    };

    /// <summary>Numeric form used by the <c>openclaw.mcp.health.state</c> metric.</summary>
    public static int ToMetricValue(McpServerHealthState state) => state switch
    {
        McpServerHealthState.Healthy => 1,
        McpServerHealthState.Degraded => 2,
        McpServerHealthState.Down => 3,
        _ => 0
    };
}

/// <summary>
/// Closed set of probe failure classes. The raw exception text never leaves the process: an
/// operator only needs to know which of these five happened (design §8.3).
/// </summary>
public static class McpProbeErrorCodes
{
    public const string Timeout = "timeout";
    public const string Transport = "transport";
    public const string Protocol = "protocol";
    public const string Unauthorized = "unauthorized";
    public const string ServerError = "server_error";

    /// <summary>Why a healthy server was demoted even though the probe succeeded.</summary>
    public const string ToolsChanged = "tools_changed";

    public static bool IsKnownCode(string? code) => code is not null && (code switch
    {
        Timeout or Transport or Protocol or Unauthorized or ServerError or ToolsChanged => true,
        _ => false
    });
}

/// <summary>
/// Immutable health record for one MCP server. Snapshots are replaced wholesale so readers never
/// observe a half-updated record.
/// </summary>
public sealed record McpServerHealth
{
    public required string ServerId { get; init; }

    public McpServerHealthState State { get; init; } = McpServerHealthState.Unknown;

    public DateTimeOffset? LastProbeAtUtc { get; init; }

    public DateTimeOffset? LastSuccessAtUtc { get; init; }

    public int ConsecutiveFailures { get; init; }

    public int ConsecutiveSuccesses { get; init; }

    /// <summary>Duration of the last successful probe; <see langword="null"/> until one succeeds.</summary>
    public long? LatencyMs { get; init; }

    /// <summary>Tool count reported by the last successful <c>tools/list</c>.</summary>
    public int ToolCount { get; init; }

    /// <summary>One of <see cref="McpProbeErrorCodes"/>; <see langword="null"/> while healthy.</summary>
    public string? LastErrorCode { get; init; }

    /// <summary>Sorted tool-name inventory from the last successful probe, used to detect changes.</summary>
    public string? ToolFingerprint { get; init; }

    public string StateName => McpServerHealthStates.ToWireValue(State);
}

/// <summary>
/// Outcome of recording one probe result.
/// </summary>
/// <param name="Health">The record after the update.</param>
/// <param name="PreviousState">State before the update, so callers can name the transition.</param>
/// <param name="StateChanged">Whether <see cref="McpServerHealth.State"/> moved.</param>
/// <param name="ToolsChanged">
/// Whether the probed tool inventory differs from the previous successful probe. The caller is
/// expected to reindex the registry when this is set (T-B5-5).
/// </param>
public readonly record struct McpHealthTransition(
    McpServerHealth Health,
    McpServerHealthState PreviousState,
    bool StateChanged,
    bool ToolsChanged)
{
    public bool IsDeterioration => StateChanged &&
        Health.State is McpServerHealthState.Degraded or McpServerHealthState.Down;

    /// <summary>Whether this transition is a recovery back to healthy.</summary>
    public bool IsRecovery => StateChanged && Health.State == McpServerHealthState.Healthy;
}

/// <summary>
/// Probe loop settings (<c>Mcp.Health</c> in configuration).
/// </summary>
public sealed class McpHealthConfig
{
    /// <summary>Whether the probe loop runs at all. Configured servers are still connected either way.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between probe rounds.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Per-probe timeout. A probe that exceeds it counts as a <c>timeout</c> failure.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 5;

    /// <summary>Consecutive failures that demote a healthy server to degraded.</summary>
    public int FailureThreshold { get; set; } = 2;

    /// <summary>Consecutive failures that take a server down (tools hidden, calls refused).</summary>
    public int DownThreshold { get; set; } = 5;

    /// <summary>Consecutive successes that bring a degraded or down server back to healthy.</summary>
    public int RecoveryThreshold { get; set; } = 3;

    /// <summary>
    /// Whether tools of a down server disappear from the model-facing tool list. Calls to them are
    /// refused with <c>mcp.server_unavailable</c> either way.
    /// </summary>
    public bool HideUnhealthyTools { get; set; } = true;

    /// <summary>Copy with every value clamped to something the state machine can use.</summary>
    public McpHealthConfig Normalize()
    {
        var failure = Math.Max(1, FailureThreshold);
        return new McpHealthConfig
        {
            Enabled = Enabled,
            IntervalSeconds = Math.Max(1, IntervalSeconds),
            ProbeTimeoutSeconds = Math.Max(1, ProbeTimeoutSeconds),
            FailureThreshold = failure,
            DownThreshold = Math.Max(failure, DownThreshold),
            RecoveryThreshold = Math.Max(1, RecoveryThreshold),
            HideUnhealthyTools = HideUnhealthyTools
        };
    }
}

/// <summary>
/// A tool that belongs to a specific MCP server, so the executor can consult that server's health.
/// </summary>
public interface IMcpServerBoundTool
{
    string McpServerId { get; }
}
