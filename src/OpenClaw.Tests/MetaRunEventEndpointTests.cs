using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Streaming;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.Gateway.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B3 over HTTP, on a host that mounts the real handlers twice — machine face and operator face —
/// exactly as production does, with test auth delegates standing in for the two chains.
/// </summary>
public sealed class MetaRunEventEndpointTests
{
    private const string MachineAuth = "machine-caller";
    private const string OperatorAuth = "viewer";
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task T_B3_4_An_after_beyond_the_last_sequence_is_rejected()
    {
        await using var harness = await Harness.StartAsync();
        var invocationId = await harness.SeedCompletedRunAsync();

        using var response = await harness.Machine.GetAsync(
            $"/api/integration/meta-invocations/{invocationId}/events?after=99",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(MetaRunEventEndpoints.AfterInvalid, body, StringComparison.Ordinal);

        // The same cursor on the operator face is rejected identically: one handler, two mounts.
        using var adminResponse = await harness.Operator.GetAsync(
            $"/admin/meta-invocations/{invocationId}/events?after=99",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, adminResponse.StatusCode);
    }

    [Fact]
    public async Task T_B3_3_An_after_cursor_returns_only_newer_events_and_repeats_exactly()
    {
        await using var harness = await Harness.StartAsync();
        var invocationId = await harness.SeedCompletedRunAsync();

        var first = await harness.GetJsonAsync(
            $"/api/integration/meta-invocations/{invocationId}/events?after=1", harness.Machine);
        var second = await harness.GetJsonAsync(
            $"/api/integration/meta-invocations/{invocationId}/events?after=1", harness.Machine);

        Assert.Equal(first, second);

        using var document = JsonDocument.Parse(first);
        var events = document.RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.NotEmpty(events);
        Assert.All(events, element => Assert.True(element.GetProperty("seq").GetInt64() > 1));
        Assert.True(document.RootElement.GetProperty("terminal").GetBoolean());
        Assert.False(document.RootElement.GetProperty("gap").GetBoolean());
    }

    [Fact]
    public async Task T_B3_7_A_run_without_a_journal_gets_a_derived_timeline_that_says_so()
    {
        await using var harness = await Harness.StartAsync();
        var invocationId = await harness.SeedCompletedRunAsync();

        // Exactly the pre-B3 situation, or a lost journal: the ledger row exists, the file does not.
        File.Delete(harness.Store.Events.PathFor(invocationId));

        var body = await harness.GetJsonAsync($"/admin/meta-invocations/{invocationId}/events");
        using var document = JsonDocument.Parse(body);

        Assert.True(document.RootElement.GetProperty("synthetic").GetBoolean());
        Assert.True(document.RootElement.GetProperty("terminal").GetBoolean());
        Assert.False(document.RootElement.GetProperty("gap").GetBoolean());

        var kinds = document.RootElement.GetProperty("events").EnumerateArray()
            .Select(element => element.GetProperty("kind").GetString())
            .ToList();
        Assert.Equal(["claim", "completed"], kinds);

        // Every derived event carries the flag on itself too, not just on the page.
        foreach (var element in document.RootElement.GetProperty("events").EnumerateArray())
            Assert.True(element.GetProperty("synthetic").GetBoolean());
    }

    [Fact]
    public async Task T_B3_11_Both_faces_answer_one_invocation_with_the_same_bytes()
    {
        await using var harness = await Harness.StartAsync();
        var invocationId = await harness.SeedCompletedRunAsync();

        var machine = await harness.GetJsonAsync(
            $"/api/integration/meta-invocations/{invocationId}/events", harness.Machine);
        var operatorFace = await harness.GetJsonAsync($"/admin/meta-invocations/{invocationId}/events");

        // Byte equality, not "looks equivalent": the two mounts share one handler, and this is what
        // stops someone from "just tweaking" one of them later.
        Assert.Equal(machine, operatorFace);

        var machineList = await harness.GetJsonAsync("/api/integration/meta-invocations", harness.Machine);
        var operatorList = await harness.GetJsonAsync("/admin/meta-invocations");
        Assert.Equal(machineList, operatorList);
    }

    [Fact]
    public async Task T_B3_5_The_stream_replays_from_a_cursor_and_stops_after_terminal()
    {
        await using var harness = await Harness.StartAsync();
        var invocationId = await harness.SeedCompletedRunAsync();

        var frames = await harness.ReadStreamAsync(
            $"/api/integration/meta-invocations/{invocationId}/events/stream?after=0",
            client: harness.Machine);

        var runEvents = frames.Where(frame => frame.Event == "run_event").ToList();
        Assert.NotEmpty(runEvents);
        Assert.Equal(["1", "2", "3", "4"], runEvents.Select(frame => frame.Id));
        Assert.Equal("terminal", frames[^1].Event);

        // Resuming from the third event must replay 4 and then terminal: no duplicates, no gap.
        var resumed = await harness.ReadStreamAsync(
            $"/api/integration/meta-invocations/{invocationId}/events/stream",
            lastEventId: "3",
            client: harness.Machine);
        Assert.Equal(["4"], resumed.Where(frame => frame.Event == "run_event").Select(frame => frame.Id));
        Assert.Equal("terminal", resumed[^1].Event);
    }

    [Fact]
    public async Task T_B3_13_A_reconnecting_reader_sees_each_event_once()
    {
        await using var harness = await Harness.StartAsync();
        var invocationId = await harness.SeedCompletedRunAsync();

        var first = await harness.ReadStreamAsync(
            $"/admin/meta-invocations/{invocationId}/events/stream");
        var firstIds = first.Where(frame => frame.Event == "run_event").Select(frame => frame.Id).ToList();
        Assert.Equal(["1", "2", "3", "4"], firstIds);

        var resumed = await harness.ReadStreamAsync(
            $"/admin/meta-invocations/{invocationId}/events/stream",
            lastEventId: firstIds[^1]);
        var resumedIds = resumed.Where(frame => frame.Event == "run_event").Select(frame => frame.Id).ToList();
        Assert.Empty(resumedIds);
        Assert.Single(resumed, frame => frame.Event == "terminal");
    }

    [Fact]
    public async Task T_B3_12_The_run_index_pages_by_cursor()
    {
        await using var harness = await Harness.StartAsync();
        for (var index = 0; index < 5; index++)
            await harness.SeedCompletedRunAsync($"key-page-{index}");

        var seen = new List<string>();
        string? url = "/admin/meta-invocations?limit=2";
        for (var page = 0; page < 5 && url is not null; page++)
        {
            var body = await harness.GetJsonAsync(url);
            using var document = JsonDocument.Parse(body);
            var items = document.RootElement.GetProperty("items").EnumerateArray().ToList();
            seen.AddRange(items.Select(item => item.GetProperty("invocationId").GetString()!));

            // A null nextCursor is omitted from the payload, so absence *is* "last page".
            var next = document.RootElement.TryGetProperty("nextCursor", out var cursorElement)
                ? cursorElement.GetString()
                : null;
            url = next is null ? null : $"/admin/meta-invocations?limit=2&cursor={Uri.EscapeDataString(next)}";
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task T_B3_12_A_corrupt_cursor_is_rejected_with_a_refresh_hint()
    {
        await using var harness = await Harness.StartAsync();

        using var response = await harness.Operator.GetAsync(
            "/admin/meta-invocations?cursor=not-a-cursor",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(MetaRunEventEndpoints.CursorInvalid, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T_B3_14_Unauthenticated_readers_are_rejected_on_both_faces()
    {
        await using var harness = await Harness.StartAsync();
        var invocationId = await harness.SeedCompletedRunAsync();

        // A client minted by the test server, with no auth header: the point is that the handlers
        // themselves refuse, not that some outer middleware does.
        using var anonymous = harness.CreateAnonymousClient();
        foreach (var path in new[]
                 {
                     $"/api/integration/meta-invocations/{invocationId}/events",
                     $"/admin/meta-invocations/{invocationId}/events",
                     $"/admin/meta-invocations/{invocationId}/events/stream",
                     "/admin/meta-invocations"
                 })
        {
            using var response = await anonymous.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // And the scope the operator face uses really is a viewer-level read: the least privileged
        // role that can still see a run, never an admin one.
        Assert.True(EndpointHelpers.IsRoleAllowed("Viewer", "admin.meta-invocations.read", out var required));
        Assert.Equal("viewer", required, ignoreCase: true);
    }

    [Fact]
    public async Task T_B3_16_The_operator_face_registers_get_only()
    {
        await using var harness = await Harness.StartAsync();

        var sources = harness.App.Services.GetRequiredService<EndpointDataSource>();
        var metaRoutes = sources.Endpoints
            .Select(endpoint => (Endpoint: endpoint, Pattern: (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? string.Empty))
            .Where(pair => pair.Pattern.StartsWith("/admin/meta-invocations", StringComparison.Ordinal))
            .ToList();

        foreach (var (endpoint, _) in metaRoutes)
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            Assert.Equal(["GET"], methods);
        }

        // Three routes: the index, the event page and the stream.
        Assert.Equal(3, metaRoutes.Count);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _storagePath;
        private readonly WebApplication _app;
        private readonly TestServer _server;

        private Harness(
            string storagePath,
            WebApplication app,
            TestServer server,
            MetaInvocationStore store)
        {
            _storagePath = storagePath;
            _app = app;
            _server = server;
            Store = store;
            Machine = CreateClient(server, MachineAuth);
            Operator = CreateClient(server, OperatorAuth);
        }

        public WebApplication App => _app;

        public HttpClient Machine { get; }

        public HttpClient Operator { get; }

        public MetaInvocationStore Store { get; }

        public static async Task<Harness> StartAsync()
        {
            var storagePath = Path.Join(
                Path.GetTempPath(), "openclaw-meta-run-events-endpoints", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(storagePath);

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddLogging(logging => logging.ClearProviders());
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default);
            });

            var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            builder.Services.AddSingleton(store);

            var app = builder.Build();

            // Machine face: scoped to the caller's own runs.
            MetaRunEventEndpoints.MapMetaInvocationEndpoints(
                app,
                "/api/integration/meta-invocations",
                ctx =>
                {
                    var failure = Authorize(ctx, MachineAuth);
                    return failure is not null
                        ? (default(MetaRunCaller), failure)
                        : (new MetaRunCaller(MachineAuth, Unrestricted: false), null);
                });

            // Operator face: unrestricted, viewer role, GET only.
            MetaRunEventEndpoints.MapMetaInvocationEndpoints(
                app,
                "/admin/meta-invocations",
                ctx =>
                {
                    var failure = Authorize(ctx, OperatorAuth);
                    return failure is not null
                        ? (default(MetaRunCaller), failure)
                        : (new MetaRunCaller(CallerId: null, Unrestricted: true), null);
                });

            await app.StartAsync();
            var server = app.GetTestServer();
            return new Harness(storagePath, app, server, store);
        }

        private static IResult? Authorize(HttpContext ctx, string expected)
        {
            var supplied = ctx.Request.Headers["X-Test-Auth"].ToString();
            return string.Equals(supplied, expected, StringComparison.Ordinal)
                ? null
                : Results.Unauthorized();
        }

        private static HttpClient CreateClient(TestServer server, string auth)
        {
            var client = server.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Test-Auth", auth);
            return client;
        }

        public HttpClient CreateAnonymousClient()
        {
            var client = _server.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            return client;
        }

        /// <summary>
        /// Seeds one finished run with a pause in the middle: claim → step.paused → resumed →
        /// completed. Four events, so a cursor assertion can be exact.
        /// </summary>
        public async Task<Guid> SeedCompletedRunAsync(string idempotencyKey = "key-run")
        {
            var claim = await Store.BeginOrGetAsync(
                MachineAuth,
                idempotencyKey,
                "hash",
                TestContext.Current.CancellationToken,
                sessionId: "session-1",
                skill: "triage-order");

            await Store.MarkAwaitingInputAsync(
                MachineAuth,
                idempotencyKey,
                new MetaInvocationPauseInfo("user_input", "confirm", "Which region?", null),
                TestContext.Current.CancellationToken);
            await Store.AppendEventAsync(
                claim.Record.InvocationId,
                [new MetaRunEventDraft(MetaRunEventKinds.Resumed, "confirm", "Running")],
                TestContext.Current.CancellationToken);
            await Store.CompleteAsync(
                MachineAuth,
                idempotencyKey,
                "done",
                TestContext.Current.CancellationToken);

            return claim.Record.InvocationId;
        }

        public async Task<string> GetJsonAsync(string url, HttpClient? client = null)
        {
            using var response = await (client ?? Operator).GetAsync(url, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }

        public readonly record struct SseFrame(string Event, string? Id, string Data);

        public async Task<List<SseFrame>> ReadStreamAsync(string url, string? lastEventId = null, HttpClient? client = null)
        {
            using var budget = new CancellationTokenSource(StreamTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (lastEventId is not null)
                request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);

            using var response = await (client ?? Operator)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

            var frames = new List<SseFrame>();
            var parser = new SseFrameParser();
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!budget.IsCancellationRequested)
            {
                var chunk = await reader.ReadLineAsync(budget.Token);
                if (chunk is null)
                    break;

                // Line-based reading loses no information: the parser reassembles the blank-line
                // frame boundary from the line endings.
                foreach (var frame in parser.Feed(chunk + "\n"))
                {
                    frames.Add(new SseFrame(frame.EventType, frame.Id, frame.Data));
                    if (frame.EventType == "terminal")
                        return frames;
                }
            }

            return frames;
        }

        public async ValueTask DisposeAsync()
        {
            Machine.Dispose();
            Operator.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            Store.Dispose();

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(_storagePath, recursive: true);
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(100);
                }
            }
        }
    }
}
