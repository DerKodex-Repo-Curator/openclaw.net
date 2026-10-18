using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B5 against a real MCP server: the real <see cref="McpClientHealthProbe"/> and the real registry
/// reconcile, so the pieces that only a live <c>tools/list</c> can exercise are covered.
/// </summary>
/// <remarks>
/// Loopback HTTP, same pattern as <c>McpServerToolRegistryTests</c>: the server is a
/// <see cref="WebApplication"/> with the MCP transport mapped, so no external process is needed.
/// </remarks>
public sealed class McpHealthProbeIntegrationTests : IAsyncLifetime
{
    private readonly List<WebApplication> _apps = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();
    }

    [Fact]
    public async Task T_B5_1c_A_real_probe_reports_healthy_and_counts_tools()
    {
        var url = await StartMcpServerAsync(() => ["echo"]);

        using var registry = CreateRegistry(url);
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());
        await registry.RegisterToolsAsync(nativeRegistry, Ct);

        var store = new McpServerHealthStore(new McpHealthConfig());
        var monitor = new McpServerHealthMonitor(
            registry, store, new McpClientHealthProbe(), NullLogger<McpServerHealthMonitor>.Instance);

        await monitor.RunRoundAsync(Ct);

        var health = store.Get("demo");
        Assert.Equal(McpServerHealthState.Healthy, health.State);
        Assert.Equal(1, health.ToolCount);
        Assert.NotNull(health.LatencyMs);
        Assert.Null(health.LastErrorCode);
    }

    [Fact]
    public async Task T_B5_5b_A_changed_inventory_degrades_and_reindexes_the_live_registry()
    {
        string[] inventory = ["echo"];
        var url = await StartMcpServerAsync(() => inventory);

        using var registry = CreateRegistry(url);
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());
        await registry.RegisterToolsAsync(nativeRegistry, Ct);

        var store = new McpServerHealthStore(new McpHealthConfig());
        var monitor = new McpServerHealthMonitor(
            registry, store, new McpClientHealthProbe(), NullLogger<McpServerHealthMonitor>.Instance);

        McpWorkspaceReloadResult? applied = null;
        monitor.ToolsChanged = (_, delta, _) =>
        {
            applied = delta;
            return Task.CompletedTask;
        };

        await monitor.RunRoundAsync(Ct);
        Assert.Equal(McpServerHealthState.Healthy, store.Get("demo").State);

        inventory = ["echo", "late"];
        await monitor.RunRoundAsync(Ct);

        var health = store.Get("demo");
        Assert.Equal(McpServerHealthState.Degraded, health.State);
        Assert.Equal(McpProbeErrorCodes.ToolsChanged, health.LastErrorCode);
        Assert.Equal(2, health.ToolCount);

        // The registry reconciled against the live server, so the delta names the new tool.
        Assert.NotNull(applied);
        Assert.Empty(applied!.RemovedToolNames);
        var added = Assert.Single(applied.AddedTools);
        Assert.Equal("demo_late", added.Name);
    }

    private static McpServerToolRegistry CreateRegistry(string url)
        => new(
            new McpPluginsConfig
            {
                Enabled = true,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["demo"] = new() { Transport = "http", Url = url }
                }
            },
            NullLogger<McpServerToolRegistry>.Instance);

    private async Task<string> StartMcpServerAsync(Func<string[]> toolNames)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation
        {
            Name = "demo",
            Version = "1.0.0"
        })
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [.. toolNames().Select(name => new Tool { Name = name, Description = name })]
            }))
            .WithCallToolHandler((_, _) => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "ok" }]
            }));

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync(Ct);
        _apps.Add(app);

        return $"{app.Urls.Single().TrimEnd('/')}/mcp";
    }
}
