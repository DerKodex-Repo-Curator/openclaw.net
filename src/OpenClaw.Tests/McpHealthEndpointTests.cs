using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway.Endpoints;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B5 over HTTP (design §8.3). The handlers are mounted directly - the same bodies the authorized
/// face calls - so the payloads and status codes asserted here are the production ones.
/// </summary>
public sealed class McpHealthEndpointTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task T_B5_3b_The_snapshot_lists_every_server_with_a_closed_error_code()
    {
        await using var host = await Host.StartAsync();

        host.Store.Track("alpha");
        host.Store.RecordSuccess("alpha", 11, ["read", "write"]);
        host.Store.Track("beta");
        host.Store.RecordFailure("beta", McpProbeErrorCodes.Timeout);
        host.Store.RecordFailure("beta", McpProbeErrorCodes.Timeout);

        using var response = await host.Client.GetAsync("/api/integration/mcp-health", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var servers = document.RootElement.GetProperty("servers");
        Assert.Equal(2, servers.GetArrayLength());

        var alpha = servers[0];
        Assert.Equal("alpha", alpha.GetProperty("id").GetString());
        Assert.Equal("healthy", alpha.GetProperty("state").GetString());
        Assert.Equal(11, alpha.GetProperty("latencyMs").GetInt64());
        Assert.Equal(2, alpha.GetProperty("toolCount").GetInt32());

        var beta = servers[1];
        Assert.Equal("beta", beta.GetProperty("id").GetString());
        Assert.Equal("degraded", beta.GetProperty("state").GetString());
        Assert.Equal(2, beta.GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal("timeout", beta.GetProperty("lastErrorCode").GetString());

        Assert.True(document.RootElement.TryGetProperty("generatedAtUtc", out _));
    }

    [Fact]
    public async Task T_B5_3c_The_stream_pushes_one_frame_per_state_change()
    {
        await using var host = await Host.StartAsync();
        host.Store.Track("alpha");
        host.Store.RecordSuccess("alpha", 9, ["read"]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/integration/mcp-health/stream");
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);

        // The opening comment is what proves the subscription is in place before any change happens.
        Assert.Equal(": connected", await ReadUntilAsync(reader, line => line.Length > 0));

        host.Store.RecordFailure("alpha", McpProbeErrorCodes.Timeout);
        host.Store.RecordFailure("alpha", McpProbeErrorCodes.Timeout);

        Assert.Equal("event: mcp_health", await ReadUntilAsync(reader, line => line.StartsWith("event:", StringComparison.Ordinal)));
        var data = await ReadUntilAsync(reader, line => line.StartsWith("data:", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(data["data:".Length..].Trim());
        var root = document.RootElement;
        Assert.Equal("alpha", root.GetProperty("server").GetProperty("id").GetString());
        Assert.Equal("degraded", root.GetProperty("server").GetProperty("state").GetString());
        Assert.Equal("healthy", root.GetProperty("previousState").GetString());
        Assert.Equal("timeout", root.GetProperty("server").GetProperty("lastErrorCode").GetString());
    }

    [Fact]
    public async Task T_B5_3d_The_stream_refuses_cleanly_when_monitoring_is_not_registered()
    {
        await using var host = await Host.StartWithoutStoreAsync();

        using var response = await host.Client.GetAsync("/api/integration/mcp-health/stream", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        Assert.Equal(McpHealthHandlers.HealthUnavailable, document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task T_B5_3e_An_empty_fleet_is_a_valid_answer_not_an_error()
    {
        await using var host = await Host.StartWithoutStoreAsync();

        using var response = await host.Client.GetAsync("/api/integration/mcp-health", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        Assert.Empty(document.RootElement.GetProperty("servers").EnumerateArray());
    }

    [Theory]
    [InlineData("healthy", null)]
    [InlineData("degraded", "degraded")]
    [InlineData("down", "down")]
    [InlineData("unknown", null)]
    public void T_B5_2c_The_response_header_only_appears_when_the_fleet_is_unhealthy(string state, string? expected)
    {
        var store = new McpServerHealthStore(new McpHealthConfig { FailureThreshold = 1, DownThreshold = 2 });
        store.Track("alpha");

        switch (state)
        {
            case "healthy":
                store.RecordSuccess("alpha", 1, ["read"]);
                break;
            case "degraded":
                store.RecordFailure("alpha", McpProbeErrorCodes.Timeout);
                break;
            case "down":
                store.RecordFailure("alpha", McpProbeErrorCodes.Timeout);
                store.RecordFailure("alpha", McpProbeErrorCodes.Timeout);
                break;
            default:
                break;
        }

        var services = new ServiceCollection();
        services.AddSingleton(store);
        using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        McpHealthHandlers.Annotate(context);

        var header = context.Response.Headers[McpHealthHandlers.StateHeaderName].ToString();
        Assert.Equal(expected ?? string.Empty, header);
    }

    private static async Task<string> ReadUntilAsync(StreamReader reader, Func<string, bool> predicate)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            Assert.NotNull(line);
            if (predicate(line!))
                return line!;
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Host(WebApplication app, HttpClient client, McpServerHealthStore store)
        {
            _app = app;
            Client = client;
            Store = store;
        }

        public HttpClient Client { get; }

        public McpServerHealthStore Store { get; }

        public static Task<Host> StartAsync() => StartAsync(registerStore: true);

        public static Task<Host> StartWithoutStoreAsync() => StartAsync(registerStore: false);

        private static async Task<Host> StartAsync(bool registerStore)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            var store = new McpServerHealthStore(new McpHealthConfig());
            if (registerStore)
                builder.Services.AddSingleton(store);

            var app = builder.Build();
            app.MapGet("/api/integration/mcp-health", (HttpContext ctx) => McpHealthHandlers.GetHealth(ctx));
            app.MapGet("/api/integration/mcp-health/stream", (HttpContext ctx) => McpHealthHandlers.StreamAsync(ctx));

            await app.StartAsync(TestContext.Current.CancellationToken);
            return new Host(app, app.GetTestClient(), store);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync(TestContext.Current.CancellationToken);
            await _app.DisposeAsync();
        }
    }
}
