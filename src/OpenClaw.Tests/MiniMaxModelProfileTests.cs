using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MiniMaxModelProfileTests
{
    [Fact]
    public void BundledProfiles_PreferFlashAndKeepM3Selectable()
    {
        var config = LoadConfig();
        using var registry = ConfiguredModelProfileRegistry.CreateInitialized(config);
        var profiles = config.Models.Profiles
            .Where(p => p.BaseUrl == "https://api.minimax.io/v1")
            .ToArray();

        Assert.Equal(new[] { "MiniMax-M3.1-Flash", "MiniMax-M3" }, profiles.Select(p => p.Model));
        Assert.Equal("minimax-m3-1-flash", profiles[0].Id);
        Assert.Equal(1_000_000, profiles[0].Capabilities!.MaxContextTokens);
        // The current runtime does not forward session effort through the SDK adapter.
        Assert.False(profiles[0].Capabilities!.SupportsReasoningEffort);
        Assert.Equal("minimax-m3", profiles[1].Id);
        Assert.All(profiles, p => Assert.Equal("openai-compatible", p.Provider));
        foreach (var id in new[] { "gpt-4-1-mini", "glm-4-7" })
        {
            Assert.True(registry.TryGet(id, out var routedProfile));
            Assert.DoesNotContain("minimax-m2-7", routedProfile!.FallbackProfileIds);
            Assert.Contains("minimax-m3-1-flash", routedProfile.FallbackProfileIds);
            Assert.All(routedProfile.FallbackProfileIds, fallback => Assert.True(registry.TryGet(fallback, out _)));
        }
    }

    [Theory]
    [InlineData("minimax-m3-1-flash", "MiniMax-M3.1-Flash", "https://api.minimax.io/v1")]
    [InlineData("minimax-m3-1-flash", "MiniMax-M3.1-Flash", "https://api.minimaxi.com/v1")]
    [InlineData("minimax-m3", "MiniMax-M3", "https://api.minimax.io/v1")]
    [InlineData("minimax-m3", "MiniMax-M3", "https://api.minimaxi.com/v1")]
    public async Task BundledProfiles_SendExactModelAndDefaultThinkingOptions(
        string profileId, string model, string endpoint)
    {
        var config = LoadConfig();
        config.Models.Profiles.Single(p => p.Id == profileId).BaseUrl = endpoint;
        using var registry = ConfiguredModelProfileRegistry.CreateInitialized(config);
        Assert.True(registry.TryGet(profileId, out var profile));
        var provider = ConfiguredModelProfileRegistry.BuildProviderConfig(config, profile!);
        Assert.Equal(endpoint, provider.Endpoint);
        Assert.Equal(model, provider.Model);

        await AssertRequestAsync(provider, model);
    }

    [Theory]
    [InlineData("MiniMax-M2.7")]
    [InlineData("MiniMax-M2.5")]
    [InlineData("custom-minimax-deployment")]
    public async Task ExplicitProfiles_PreserveLegacyAndCustomModelIds(string model)
    {
        var config = LoadConfig();
        config.Models.Profiles.Add(new ModelProfileConfig
        {
            Id = "user-defined",
            Provider = "openai-compatible",
            Model = model,
            BaseUrl = "https://api.minimax.io/v1"
        });
        using var registry = ConfiguredModelProfileRegistry.CreateInitialized(config);
        Assert.True(registry.TryGet("user-defined", out var profile));
        await AssertRequestAsync(ConfiguredModelProfileRegistry.BuildProviderConfig(config, profile!), model);
    }

    [Theory]
    [InlineData("minimax-m3-1-flash")]
    [InlineData("minimax-m3")]
    public async Task BundledProfiles_PreserveImageContent(string profileId)
    {
        var config = LoadConfig();
        using var registry = ConfiguredModelProfileRegistry.CreateInitialized(config);
        Assert.True(registry.TryGet(profileId, out var profile));
        await AssertRequestAsync(ConfiguredModelProfileRegistry.BuildProviderConfig(config, profile!), profile!.ModelId, image: true);
    }

    private static async Task AssertRequestAsync(LlmProviderConfig provider, string model, bool image = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        JsonElement? captured = null;
        string? authorization = null;
        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            captured = body.RootElement.Clone();
            authorization = context.Request.Headers.Authorization.ToString();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""
                {"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"test","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """, context.RequestAborted);
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);

        // Regional configuration is asserted before redirecting this real SDK request to loopback.
        provider.Endpoint = app.Urls.Single() + "/v1";
        provider.ApiKey = "test-minimax-key";
        using var client = LlmClientFactory.CreateChatClient(provider);
        var options = new ChatOptions();

        var message = image
            ? new ChatMessage(ChatRole.User, [new TextContent("Describe this image"), new UriContent("https://example.com/test.png", "image/png")])
            : new ChatMessage(ChatRole.User, "Say ok");
        var response = await client.GetResponseAsync([message], options, timeout.Token);

        Assert.Equal("ok", response.Text);
        Assert.Equal("Bearer test-minimax-key", authorization);
        var request = Assert.IsType<JsonElement>(captured);
        Assert.Equal(model, request.GetProperty("model").GetString());
        if (image)
        {
            var content = request.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal("image_url", content[1].GetProperty("type").GetString());
            Assert.Equal("https://example.com/test.png", content[1].GetProperty("image_url").GetProperty("url").GetString());
        }
        Assert.False(request.TryGetProperty("thinking", out _));
        Assert.False(request.TryGetProperty("reasoning_split", out _));
        Assert.False(request.TryGetProperty("service_tier", out _));
        Assert.False(request.TryGetProperty("reasoning_effort", out _));
    }

    private static GatewayConfig LoadConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenClaw.Net.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(directory.FullName, "src", "OpenClaw.Gateway", "appsettings.json"))
            .Build();
        return configuration.GetSection("OpenClaw").Get<GatewayConfig>()!;
    }
}
