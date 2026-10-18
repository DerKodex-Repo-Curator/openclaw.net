using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// T-B5-10 documentation gate: an operator who sees `mcp.server_unavailable` or a `down` server needs
/// the thresholds and the endpoints written down, not inferred from the code.
/// </summary>
public sealed class McpHealthDocumentationTests
{
    [Fact]
    public void T_B5_10_The_integration_docs_describe_the_endpoints_states_and_thresholds()
    {
        var root = FindRepositoryRoot();
        var english = File.ReadAllText(
            Path.Combine(root, "docs", "integrations", "meta-skill-invocation-api.md"));

        foreach (var token in new[]
                 {
                     "GET /api/integration/mcp-health",
                     "GET /api/integration/mcp-health/stream",
                     "X-OpenClaw-Mcp-State",
                     "mcp.server_unavailable",
                     "event: mcp_health",
                     McpServerHealthStates.Unknown,
                     McpServerHealthStates.Healthy,
                     McpServerHealthStates.Degraded,
                     McpServerHealthStates.Down,
                     McpProbeErrorCodes.Timeout,
                     McpProbeErrorCodes.Transport,
                     McpProbeErrorCodes.Protocol,
                     McpProbeErrorCodes.Unauthorized,
                     McpProbeErrorCodes.ServerError,
                     McpProbeErrorCodes.ToolsChanged,
                     "FailureThreshold",
                     "DownThreshold",
                     "RecoveryThreshold",
                     "HideUnhealthyTools"
                 })
        {
            Assert.Contains(token, english, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void T_B5_10b_The_chinese_integration_page_carries_the_same_contract()
    {
        var root = FindRepositoryRoot();
        var chinese = File.ReadAllText(
            Path.Combine(root, "docs", "zh-CN", "integrations", "meta-skill-invocation-api.md"));

        foreach (var token in new[]
                 {
                     "GET /api/integration/mcp-health",
                     "/mcp-health/stream",
                     "X-OpenClaw-Mcp-State",
                     "mcp.server_unavailable",
                     "Mcp.Health.Enabled",
                     "HideUnhealthyTools"
                 })
        {
            Assert.Contains(token, chinese, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_wire_states_and_codes_are_the_http_contract()
    {
        // Changing a value here without changing the docs would be a silent breaking change.
        Assert.Equal("unknown", McpServerHealthStates.ToWireValue(McpServerHealthState.Unknown));
        Assert.Equal("healthy", McpServerHealthStates.ToWireValue(McpServerHealthState.Healthy));
        Assert.Equal("degraded", McpServerHealthStates.ToWireValue(McpServerHealthState.Degraded));
        Assert.Equal("down", McpServerHealthStates.ToWireValue(McpServerHealthState.Down));
        Assert.Equal(McpServerHealthState.Down, McpServerHealthStates.Parse("DOWN"));

        Assert.Equal(0, McpServerHealthStates.ToMetricValue(McpServerHealthState.Unknown));
        Assert.Equal(1, McpServerHealthStates.ToMetricValue(McpServerHealthState.Healthy));
        Assert.Equal(2, McpServerHealthStates.ToMetricValue(McpServerHealthState.Degraded));
        Assert.Equal(3, McpServerHealthStates.ToMetricValue(McpServerHealthState.Down));

        Assert.False(McpProbeErrorCodes.IsKnownCode("connection refused by 10.0.0.1:9999"));
        Assert.True(McpProbeErrorCodes.IsKnownCode(McpProbeErrorCodes.ToolsChanged));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(current.FullName, "docs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
