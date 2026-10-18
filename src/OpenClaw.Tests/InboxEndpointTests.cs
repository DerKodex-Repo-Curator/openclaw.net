using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.Gateway.Inbox;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B7 over HTTP (design §10.3). Handlers are mounted directly - the same bodies the authorized face
/// calls - so the status codes and payloads asserted here are the production ones.
/// </summary>
public sealed class InboxEndpointTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_list_returns_items_with_active_counts()
    {
        await using var host = await Host.StartAsync();
        await host.OpenAsync(InboxKinds.RunFailed, "run-1");
        await host.OpenAsync(InboxKinds.Approval, "run-2");

        using var response = await host.Client.GetAsync("/api/inbox", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(2, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(2, document.RootElement.GetProperty("counts").GetProperty("open").GetInt32());
    }

    [Fact]
    public async Task Ack_answers_200_then_404_and_409_for_the_impossible_cases()
    {
        await using var host = await Host.StartAsync();
        var item = await host.OpenAsync(InboxKinds.RunPaused, "run-1");

        using var ok = await host.Client.PostAsync($"/api/inbox/{item.Id}/ack", null, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Idempotent: the second ack is still a 200 and still Acknowledged.
        using var again = await host.Client.PostAsync($"/api/inbox/{item.Id}/ack", null, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(
            InboxStates.Acknowledged,
            JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct))
                .RootElement.GetProperty("state").GetString());

        using var missing = await host.Client.PostAsync("/api/inbox/ib_nope/ack", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await host.Store!.ResolveByResourceAsync(InboxKinds.RunPaused, "run-1", InboxResolutionCodes.Completed, Ct);
        using var closed = await host.Client.PostAsync($"/api/inbox/{item.Id}/ack", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
    }

    [Fact]
    public async Task T_B7_7_Resolving_a_resource_driven_item_is_a_409_with_a_closed_code()
    {
        await using var host = await Host.StartAsync();
        var item = await host.OpenAsync(InboxKinds.McpDown, "alpha");

        using var response = await host.Client.PostAsync($"/api/inbox/{item.Id}/resolve", null, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(InboxErrorCodes.ResourceDriven, document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_notification_item_can_be_resolved_by_a_person()
    {
        await using var host = await Host.StartAsync();
        var item = await host.OpenAsync(InboxKinds.Notification, "notification:n_1");

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/inbox/{item.Id}/resolve",
            new { resolutionCode = InboxResolutionCodes.Resolved },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Snooze_rejects_a_target_in_the_past_with_400()
    {
        await using var host = await Host.StartAsync();
        var item = await host.OpenAsync(InboxKinds.RunPaused, "run-1");

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/inbox/{item.Id}/snooze",
            new { untilUtc = DateTimeOffset.UtcNow.AddMinutes(-5) },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(InboxErrorCodes.SnoozeInPast, document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task T_B7_9_Ingest_stores_one_item_per_envelope_id_and_ignores_replays()
    {
        await using var host = await Host.StartAsync();

        var envelope = new
        {
            schemaVersion = 2,
            id = "n_abcdef0123456789abcdef01",
            category = "core.outbox.dead_letter",
            severity = "Warning",
            subjectStableId = "abc123",
            resource = new { kind = "outbox", id = "9f8e7d6c5b4a" },
            message = "Outbox item was dead-lettered.",
            attributes = new Dictionary<string, string> { ["stepId"] = "step-refund" },
            occurredAtUtc = DateTimeOffset.UtcNow
        };

        using var first = await host.Client.PostAsJsonAsync("/api/integration/inbox/items", envelope, Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The sender's retry schedule carries the same id; it must not add a second todo.
        using var retry = await host.Client.PostAsJsonAsync("/api/integration/inbox/items", envelope, Ct);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        var items = await host.Store!.SnapshotAsync(Ct);
        var item = Assert.Single(items);
        Assert.Equal(InboxKinds.Notification, item.Kind);
        Assert.Equal("n_abcdef0123456789abcdef01", item.Subject.NotificationId);
        Assert.Equal("step-refund", item.Subject.StepId);
        Assert.Equal(InboxSeverities.Warning, item.Severity);
    }

    [Fact]
    public async Task Ingest_rejects_a_payload_that_is_not_a_v2_envelope()
    {
        await using var host = await Host.StartAsync();

        using var noId = await host.Client.PostAsJsonAsync(
            "/api/integration/inbox/items",
            new { schemaVersion = 2, category = "core.outbox.dead_letter", severity = "Info" },
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);

        using var v1 = await host.Client.PostAsJsonAsync(
            "/api/integration/inbox/items",
            new { id = "n_1", category = "core.outbox.dead_letter", severity = "Info" },
            Ct);
        // schemaVersion defaults to 1 in the DTO, so a Phase 2 body cannot create a todo by accident.
        Assert.Equal(HttpStatusCode.BadRequest, v1.StatusCode);

        Assert.Empty(await host.Store!.SnapshotAsync(Ct));
    }

    [Fact]
    public async Task T_B7_8_The_stream_pushes_frames_and_resumes_from_Last_Event_ID()
    {
        await using var host = await Host.StartAsync();

        // Every wait below is bounded: a stream test that hangs teaches nothing.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/inbox/stream");
        using var response = await host.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStreamAsync(budget.Token);
        var reader = new StreamReader(body);

        // Subscribed before anything changes: the opening comment is what proves that.
        Assert.Equal(": connected", await ReadUntilAsync(reader, line => line.Length > 0, budget.Token));
        Assert.Equal(1, host.Stream.SubscriberCount);

        var opened = await host.OpenAsync(InboxKinds.RunPaused, "run-1");
        Assert.Equal(1, host.Stream.LastSeq);

        // Frames are read in wire order: id, event, data. Reading out of order would silently
        // swallow the id line and then wait for one that never comes.
        var idLine = await ReadUntilAsync(
            reader, line => line.StartsWith("id:", StringComparison.Ordinal), budget.Token);
        var seq = long.Parse(idLine["id:".Length..].Trim(), CultureInfo.InvariantCulture);
        Assert.True(seq >= 1);

        var eventLine = await ReadUntilAsync(
            reader, line => line.StartsWith("event:", StringComparison.Ordinal), budget.Token);
        Assert.Equal($"event: {InboxHandlers.StreamEventName}", eventLine);

        var dataLine = await ReadUntilAsync(
            reader, line => line.StartsWith("data:", StringComparison.Ordinal), budget.Token);
        Assert.Contains(opened.Id, dataLine, StringComparison.Ordinal);

        // Hanging up detaches the subscription; the changes below happen while nobody listens.
        reader.Dispose();

        await host.OpenAsync(InboxKinds.RunPaused, "run-2");
        await host.OpenAsync(InboxKinds.RunPaused, "run-3");

        using var resumeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/inbox/stream");
        resumeRequest.Headers.TryAddWithoutValidation("Last-Event-ID", seq.ToString(CultureInfo.InvariantCulture));
        using var resume = await host.Client.SendAsync(
            resumeRequest, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        var resumeBody = await resume.Content.ReadAsStreamAsync(budget.Token);
        using var resumeReader = new StreamReader(resumeBody);
        await ReadUntilAsync(resumeReader, line => line.Length > 0, budget.Token);

        var firstReplayedId = await ReadUntilAsync(
            resumeReader, line => line.StartsWith("id:", StringComparison.Ordinal), budget.Token);
        var replayedSeq = long.Parse(firstReplayedId["id:".Length..].Trim(), CultureInfo.InvariantCulture);

        // No duplicate of the frame already seen: the resume starts strictly after it.
        Assert.Equal(seq + 1, replayedSeq);
    }

    [Fact]
    public async Task The_surface_answers_404_with_a_closed_code_when_it_is_switched_off()
    {
        await using var host = await Host.StartWithoutStoreAsync();

        using var response = await host.Client.GetAsync("/api/inbox", Ct);
        var payload = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(InboxErrorCodes.Disabled, document.RootElement.GetProperty("error").GetString());
    }


    /// <summary>
    /// Runs a handler that returns <see cref="IResult"/> as a <see cref="RequestDelegate"/>.
    /// </summary>
    /// <remarks>
    /// Without this, a single-parameter lambda returning <c>Task&lt;IResult&gt;</c> binds to the
    /// <c>RequestDelegate</c> overload and its result is silently discarded - the ASP0016 warning is
    /// exactly this mistake, and the symptom is a 200 with an empty body.
    /// </remarks>
    private static async Task ExecuteAsync(HttpContext ctx, Task<IResult> pending)
        => await (await pending).ExecuteAsync(ctx);

    private static async Task<string> ReadUntilAsync(
        StreamReader reader,
        Func<string, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            Assert.NotNull(line);
            if (predicate(line))
                return line;
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly string root;

        private Host(WebApplication app, HttpClient client, InboxStore? store, InboxStream stream, string root)
        {
            this.app = app;
            Client = client;
            Store = store;
            Stream = stream;
            this.root = root;
        }

        public HttpClient Client { get; }

        public InboxStore? Store { get; }

        public InboxStream Stream { get; }

        public static async Task<Host> StartAsync() => await StartAsync(withStore: true);

        public static async Task<Host> StartWithoutStoreAsync() => await StartAsync(withStore: false);

        public async Task<InboxItem> OpenAsync(string kind, string resourceKey)
        {
            var (item, _) = await Store!.OpenAsync(
                new InboxOpenRequest
                {
                    Kind = kind,
                    Subject = new InboxSubject { ResourceKey = resourceKey },
                    OpenedSeq = 1
                },
                Ct);

            return item;
        }

        public static async Task<Host> StartAsync(bool withStore)
        {
            var root = Path.Combine(Path.GetTempPath(), "openclaw-inbox-endpoints", Guid.NewGuid().ToString("N"));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            var stream = new InboxStream();
            InboxStore? store = null;
            if (withStore)
            {
                store = new InboxStore(new InboxConfig(), root, stream);
                builder.Services.AddSingleton(store);
            }

            builder.Services.AddSingleton(stream);

            var app = builder.Build();
            app.MapGet(
                "/api/inbox",
                (HttpContext ctx) => ExecuteAsync(ctx, InboxHandlers.ListAsync(ctx, ctx.RequestAborted)));
            app.MapGet("/api/inbox/stream", (HttpContext ctx) => InboxHandlers.StreamAsync(ctx));
            app.MapPost(
                "/api/inbox/{id}/ack",
                (HttpContext ctx, string id) => InboxHandlers.AcknowledgeAsync(ctx, id, ctx.RequestAborted));
            app.MapPost(
                "/api/inbox/{id}/snooze",
                (HttpContext ctx, string id) => InboxHandlers.SnoozeAsync(ctx, id, ctx.RequestAborted));
            app.MapPost(
                "/api/inbox/{id}/resolve",
                (HttpContext ctx, string id) => InboxHandlers.ResolveAsync(ctx, id, ctx.RequestAborted));
            app.MapPost(
                "/api/integration/inbox/items",
                (HttpContext ctx) => ExecuteAsync(ctx, InboxHandlers.IngestAsync(ctx, ctx.RequestAborted)));

            await app.StartAsync(TestContext.Current.CancellationToken);
            return new Host(app, app.GetTestClient(), store, stream, root);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();

            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
