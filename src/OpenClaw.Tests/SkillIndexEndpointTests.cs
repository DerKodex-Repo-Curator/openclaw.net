using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway.Endpoints;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B4 over HTTP (design §7.3, T-B4-1/3/4/8). The handlers are mounted directly - the same bodies the
/// two authorized faces call - so the status codes and payloads asserted here are the production ones.
/// </summary>
public sealed class SkillIndexEndpointTests
{
    [Fact]
    public async Task T_B4_1_Readiness_reports_the_skill_section_of_a_published_index()
    {
        await using var host = await Host.StartAsync(ready: true);

        using var response = await host.Client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        Assert.Equal("ready", document.RootElement.GetProperty("status").GetString());

        var skills = document.RootElement.GetProperty("skills");
        Assert.Equal("ready", skills.GetProperty("state").GetString());
        Assert.Equal(1, skills.GetProperty("version").GetInt64());
        Assert.Equal(0, skills.GetProperty("diagnostics").GetInt32());
        Assert.StartsWith("sha256:", skills.GetProperty("fingerprint").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_B4_4_An_unpublished_index_makes_readiness_fail()
    {
        await using var host = await Host.StartAsync(ready: false);

        using var response = await host.Client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        Assert.Equal("not_ready", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("unready", document.RootElement.GetProperty("skills").GetProperty("state").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("skills").GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task T_B4_3b_A_degraded_index_stays_ready_unless_the_operator_asks_for_strictness()
    {
        await using var host = await Host.StartAsync(ready: true, breakOneSkill: true);

        using var lenient = await host.Client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var lenientBody = await lenient.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, lenient.StatusCode);
        using (var document = JsonDocument.Parse(lenientBody))
        {
            var skills = document.RootElement.GetProperty("skills");
            Assert.Equal("degraded", skills.GetProperty("state").GetString());
            Assert.Equal(1, skills.GetProperty("diagnostics").GetInt32());
            Assert.Equal(1, skills.GetProperty("version").GetInt64());
        }

        // FailOnDegradedSkills=true is the strict container-probe mode.
        using var strict = await host.StrictClient.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, strict.StatusCode);
    }

    [Fact]
    public async Task T_B4_1b_The_skill_index_endpoint_lists_skills_and_diagnostics()
    {
        await using var host = await Host.StartAsync(ready: true, breakOneSkill: true);

        using var response = await host.Client.GetAsync("/api/integration/skill-index", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("version").GetInt64());
        Assert.True(root.GetProperty("ready").GetBoolean());
        Assert.Equal("degraded", root.GetProperty("state").GetString());
        Assert.True(root.GetProperty("lastAttemptVersion").GetInt64() > 1);
        Assert.StartsWith("sha256:", root.GetProperty("fingerprint").GetString()!, StringComparison.Ordinal);

        var skills = root.GetProperty("skills").EnumerateArray().ToList();
        var alpha = Assert.Single(skills, s => s.GetProperty("name").GetString() == "alpha");
        Assert.Equal("standard", alpha.GetProperty("kind").GetString());
        Assert.Equal("extra", alpha.GetProperty("source").GetString());
        Assert.Equal("ready", alpha.GetProperty("status").GetString());

        // The broken directory has no trustworthy name, so it is reported by directory name.
        var broken = Assert.Single(skills, s => s.GetProperty("name").GetString() == "broken");
        Assert.Equal("invalid", broken.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(broken.GetProperty("diagnosticCode").GetString()));

        var diagnostics = root.GetProperty("diagnostics").EnumerateArray().ToList();
        var diagnostic = Assert.Single(diagnostics);
        Assert.True(diagnostic.GetProperty("fatal").GetBoolean());
        Assert.Contains("broken", diagnostic.GetProperty("path").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_B4_2b_Reindex_reports_both_versions_so_a_rejected_attempt_is_visible()
    {
        await using var host = await Host.StartAsync(ready: true);

        using var first = await host.Client.PostAsync("/admin/skills/reindex", content: null, TestContext.Current.CancellationToken);
        var firstBody = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using (var document = JsonDocument.Parse(firstBody))
        {
            Assert.Equal(2, document.RootElement.GetProperty("version").GetInt64());
            Assert.Equal(1, document.RootElement.GetProperty("previousVersion").GetInt64());
            Assert.Equal("ready", document.RootElement.GetProperty("state").GetString());
        }

        // Break a skill, reindex again: the version does not move, and the response says why.
        File.WriteAllText(Path.Combine(host.Root, "alpha", "SKILL.md"), "# broken");

        using var second = await host.Client.PostAsync("/admin/skills/reindex", content: null, TestContext.Current.CancellationToken);
        var secondBody = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using (var document = JsonDocument.Parse(secondBody))
        {
            Assert.Equal(2, document.RootElement.GetProperty("version").GetInt64());
            Assert.Equal(2, document.RootElement.GetProperty("previousVersion").GetInt64());
            Assert.Equal("degraded", document.RootElement.GetProperty("state").GetString());
            var diagnostics = document.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Single(diagnostics);
        }
    }

    [Fact]
    public async Task T_B4_8_A_degraded_index_is_announced_on_every_answer_that_used_it()
    {
        await using var host = await Host.StartAsync(ready: true, breakOneSkill: true);

        using var response = await host.Client.GetAsync("/annotated", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "degraded",
            Assert.Single(response.Headers.GetValues(SkillIndexHandlers.HeaderName)));
    }

    [Fact]
    public async Task T_B4_2c_Reindex_without_a_compiler_is_a_clear_503()
    {
        await using var app = BuildApp(compiler: null, failOnDegraded: false);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync("/admin/skills/reindex", content: null, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("compiler is not registered", body, StringComparison.OrdinalIgnoreCase);
    }

    private static WebApplication BuildApp(SkillIndexCompiler? compiler, bool failOnDegraded)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));

        if (compiler is not null)
            builder.Services.AddSingleton(compiler);

        var app = builder.Build();
        app.MapGet("/health/ready", (HttpContext ctx) => SkillIndexHandlers.Readiness(ctx, failOnDegraded));
        app.MapGet("/api/integration/skill-index", (HttpContext ctx) => SkillIndexHandlers.GetIndex(ctx, null));
        app.MapPost("/admin/skills/reindex", (HttpContext ctx) => SkillIndexHandlers.ReindexAsync(ctx));
        app.MapGet("/annotated", (HttpContext ctx) =>
        {
            SkillIndexHandlers.Annotate(ctx);
            return Results.Ok();
        });

        return app;
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Host(
            WebApplication app,
            HttpClient client,
            HttpClient strictClient,
            SkillIndexCompiler compiler,
            string root)
        {
            _app = app;
            Client = client;
            StrictClient = strictClient;
            Compiler = compiler;
            Root = root;
        }

        public HttpClient Client { get; }

        public HttpClient StrictClient { get; }

        public SkillIndexCompiler Compiler { get; }

        public string Root { get; }

        public static async Task<Host> StartAsync(bool ready, bool breakOneSkill = false)
        {
            var root = Path.Join(Path.GetTempPath(), "openclaw-skill-index-http", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            if (ready)
            {
                WriteSkill(root, "alpha");
                WriteSkill(root, "beta");
            }
            else
            {
                BreakSkill(root, "broken");
            }

            var compiler = new SkillIndexCompiler(
                new SkillsConfig
                {
                    Load = new SkillLoadConfig
                    {
                        ExtraDirs = [root],
                        IncludeBundled = false,
                        IncludeManaged = false,
                        IncludeWorkspace = false
                    }
                },
                NullLogger<SkillIndexCompiler>.Instance);

            await compiler.CompileAsync(SkillIndexCompileTrigger.Startup, TestContext.Current.CancellationToken);

            // Degraded is a last-known-good state: the index must publish cleanly first, then break.
            if (ready && breakOneSkill)
            {
                BreakSkill(root, "broken");
                await compiler.CompileAsync(SkillIndexCompileTrigger.FileChange, TestContext.Current.CancellationToken);
            }

            var lenient = BuildApp(compiler, failOnDegraded: false);
            var strict = BuildApp(compiler, failOnDegraded: true);
            await lenient.StartAsync(TestContext.Current.CancellationToken);
            await strict.StartAsync(TestContext.Current.CancellationToken);

            return new Host(lenient, lenient.GetTestClient(), strict.GetTestClient(), compiler, root);
        }

        private static void BreakSkill(string root, string name)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), "# broken");
        }

        private static void WriteSkill(string root, string name)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            File.WriteAllText(
                Path.Combine(directory, "SKILL.md"),
                $"""
                 ---
                 name: {name}
                 description: {name} skill
                 ---

                 Body for {name}.
                 """);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
