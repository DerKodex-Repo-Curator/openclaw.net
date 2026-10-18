using OpenClaw.Core.Plugins;

namespace OpenClaw.Core.Models;

/// <summary>
/// One MCP server in <c>GET /api/integration/mcp-health</c>.
/// </summary>
public sealed record McpServerHealthEntry
{
    public required string Id { get; init; }

    /// <summary><c>unknown</c> / <c>healthy</c> / <c>degraded</c> / <c>down</c>.</summary>
    public required string State { get; init; }

    public DateTimeOffset? LastProbeAtUtc { get; init; }

    public DateTimeOffset? LastSuccessAtUtc { get; init; }

    public int ConsecutiveFailures { get; init; }

    /// <summary>Duration of the last successful probe.</summary>
    public long? LatencyMs { get; init; }

    public int ToolCount { get; init; }

    /// <summary>
    /// A closed code (<c>timeout</c> / <c>transport</c> / <c>protocol</c> / <c>unauthorized</c> /
    /// <c>server_error</c> / <c>tools_changed</c>), never raw exception text.
    /// </summary>
    public string? LastErrorCode { get; init; }
}

/// <summary>
/// Response for <c>GET /api/integration/mcp-health</c>.
/// </summary>
public sealed record McpHealthResponse
{
    public DateTimeOffset GeneratedAtUtc { get; init; }

    public IReadOnlyList<McpServerHealthEntry> Servers { get; init; } = [];
}

/// <summary>
/// One <c>event: mcp_health</c> frame on <c>GET /api/integration/mcp-health/stream</c>. Pushed only
/// when a state actually changes, so a quiet stream means a quiet fleet (design §8.3).
/// </summary>
public sealed record McpHealthStreamEvent
{
    public required McpServerHealthEntry Server { get; init; }

    /// <summary>The state this server held before the change.</summary>
    public required string PreviousState { get; init; }

    /// <summary>Whether the change was caused by a different tool inventory rather than a failure.</summary>
    public bool ToolsChanged { get; init; }

    public DateTimeOffset OccurredAtUtc { get; init; }
}

/// <summary>Maps <see cref="McpServerHealth"/> records onto the wire contract.</summary>
public static class McpHealthProjection
{
    public static McpHealthResponse ToResponse(IReadOnlyList<McpServerHealth> servers, DateTimeOffset generatedAtUtc)
        => new()
        {
            GeneratedAtUtc = generatedAtUtc,
            Servers = [.. servers.Select(ToEntry)]
        };

    public static McpServerHealthEntry ToEntry(McpServerHealth health)
        => new()
        {
            Id = health.ServerId,
            State = health.StateName,
            LastProbeAtUtc = health.LastProbeAtUtc,
            LastSuccessAtUtc = health.LastSuccessAtUtc,
            ConsecutiveFailures = health.ConsecutiveFailures,
            LatencyMs = health.LatencyMs,
            ToolCount = health.ToolCount,
            LastErrorCode = health.LastErrorCode
        };
}
