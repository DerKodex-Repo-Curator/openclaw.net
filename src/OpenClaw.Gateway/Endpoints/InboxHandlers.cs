using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Inbox;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Inbox;

namespace OpenClaw.Gateway.Endpoints;

/// <summary>
/// B7 inbox surface: list, acknowledge, snooze, resolve, stream, ingest and rebuild.
/// </summary>
/// <remarks>
/// <para>
/// No authorization here - the mounts in <see cref="InboxEndpoints"/> and
/// <see cref="AdminEndpoints"/> own that - so a test can drive a handler against a hand-built store
/// without standing up a gateway.
/// </para>
/// <para>
/// The rule this surface exists to enforce: a notification may be lost, an item may not. Nothing here
/// writes to the run ledger or the notification path; an item is only ever the projection of
/// something the gateway already knows (design §10.4).
/// </para>
/// </remarks>
internal static class InboxHandlers
{
    internal const string StreamEventName = "inbox_item";
    internal const string GapEventName = "inbox_gap";

    /// <summary>How long a stream waits for a change before checking whether the client left.</summary>
    private const int StreamKeepAliveMilliseconds = 15_000;

    internal static InboxStore? ResolveStore(HttpContext ctx) => ctx.RequestServices.GetService<InboxStore>();

    internal static InboxStream? ResolveStream(HttpContext ctx) => ctx.RequestServices.GetService<InboxStream>();

    internal static InboxProjector? ResolveProjector(HttpContext ctx)
        => ctx.RequestServices.GetService<InboxProjector>();

    /// <summary>Unified "the surface is switched off" answer: 404 with a closed error code.</summary>
    internal static IResult Disabled()
        => Results.Json(
            new OperationStatusResponse { Success = false, Error = InboxErrorCodes.Disabled },
            CoreJsonContext.Default.OperationStatusResponse,
            statusCode: StatusCodes.Status404NotFound);

    internal static IResult FromMutation(InboxMutation mutation)
    {
        if (mutation.Succeeded)
        {
            return Results.Json(mutation.Item, CoreJsonContext.Default.InboxItem);
        }

        var status = mutation.ErrorCode switch
        {
            InboxErrorCodes.NotFound => StatusCodes.Status404NotFound,
            InboxErrorCodes.ResourceDriven => StatusCodes.Status409Conflict,
            InboxErrorCodes.InvalidState => StatusCodes.Status409Conflict,
            InboxErrorCodes.SnoozeInPast => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest
        };

        return Results.Json(
            new OperationStatusResponse { Success = false, Error = mutation.ErrorCode },
            CoreJsonContext.Default.OperationStatusResponse,
            statusCode: status);
    }

    internal static async Task<IResult> ListAsync(HttpContext ctx, CancellationToken cancellationToken)
    {
        var store = ResolveStore(ctx);
        if (store is null)
            return Disabled();

        var limit = int.TryParse(ctx.Request.Query["limit"], out var parsed) ? parsed : (int?)null;
        var response = await store.ListAsync(
            ctx.Request.Query["state"],
            ctx.Request.Query["kind"],
            ctx.Request.Query["cursor"],
            limit,
            cancellationToken).ConfigureAwait(false);

        return Results.Json(response, CoreJsonContext.Default.InboxListResponse);
    }

    internal static async Task<IResult> AcknowledgeAsync(
        HttpContext ctx,
        string id,
        CancellationToken cancellationToken)
    {
        var store = ResolveStore(ctx);
        if (store is null)
            return Disabled();

        return FromMutation(await store.AcknowledgeAsync(id, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<IResult> SnoozeAsync(
        HttpContext ctx,
        string id,
        CancellationToken cancellationToken)
    {
        var store = ResolveStore(ctx);
        if (store is null)
            return Disabled();

        InboxSnoozeRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                CoreJsonContext.Default.InboxSnoozeRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return BadRequest();
        }

        if (request is null)
            return BadRequest();

        return FromMutation(
            await store.SnoozeAsync(id, request.UntilUtc, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<IResult> ResolveAsync(
        HttpContext ctx,
        string id,
        CancellationToken cancellationToken)
    {
        var store = ResolveStore(ctx);
        if (store is null)
            return Disabled();

        InboxResolveRequest? request = null;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                CoreJsonContext.Default.InboxResolveRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // An empty body is the normal case for a human closing their own todo.
        }

        var code = string.IsNullOrWhiteSpace(request?.ResolutionCode)
            ? InboxResolutionCodes.Resolved
            : request!.ResolutionCode!;

        if (!InboxResolutionCodes.All.Contains(code, StringComparer.Ordinal))
            return BadRequest();

        return FromMutation(await store.ResolveAsync(id, code, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Pushes one <c>inbox_item</c> frame per change, framing <c>id:</c> with the sequence so a
    /// reconnecting dashboard can resume with <c>Last-Event-ID</c> (T-B7-8).
    /// </summary>
    internal static async Task StreamAsync(HttpContext ctx)
    {
        var stream = ResolveStream(ctx);
        if (stream is null)
        {
            await Disabled().ExecuteAsync(ctx).ConfigureAwait(false);
            return;
        }

        var cancellationToken = ctx.RequestAborted;
        var lastEventSeq = ParseLastEventId(ctx.Request.Headers["Last-Event-ID"].ToString());
        using var subscription = stream.Subscribe(lastEventSeq, out var gap);

        try
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            await ctx.Response.WriteAsync(": connected\n\n", cancellationToken).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (gap)
            {
                // The ring no longer holds what the caller missed: say so, and let it refetch the
                // list rather than pretend a partial replay is a complete one.
                await WriteRawAsync(
                    ctx,
                    $"event: {GapEventName}\ndata: {{\"lastSeq\":{stream.LastSeq}}}\n\n",
                    cancellationToken).ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(StreamKeepAliveMilliseconds);

                InboxStreamFrame frame;
                try
                {
                    frame = await subscription.Reader.ReadAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await WriteRawAsync(ctx, ": keepalive\n\n", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (System.Threading.Channels.ChannelClosedException)
                {
                    // The hub detached this subscriber (buffer overflow); the client reconnects with
                    // its Last-Event-ID and gets an explicit gap if it fell too far behind.
                    break;
                }

                var payload = JsonSerializer.Serialize(
                    new InboxStreamEvent(frame.Seq, frame.Change, frame.Item),
                    CoreJsonContext.Default.InboxStreamEvent);

                await WriteRawAsync(
                    ctx,
                    $"id: {frame.EventId}\nevent: {StreamEventName}\ndata: {payload}\n\n",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // Broken pipe: same story, and there is nobody left to tell.
        }
    }

    /// <summary>
    /// <c>POST /api/integration/inbox/items</c>: the DrasiWake <c>inbox</c> channel's receiver.
    /// </summary>
    /// <remarks>
    /// The envelope's own id is the de-duplication key, so the sender's retry schedule (same
    /// <c>Idempotency-Key</c> on every attempt) collapses onto one item. Attributes are not stored
    /// wholesale - only <c>stepId</c> is, because it is the one field an operator can click through.
    /// </remarks>
    internal static async Task<IResult> IngestAsync(HttpContext ctx, CancellationToken cancellationToken)
    {
        var store = ResolveStore(ctx);
        if (store is null)
            return Disabled();

        InboxNotificationEnvelope? envelope;
        try
        {
            envelope = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                CoreJsonContext.Default.InboxNotificationEnvelope,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return BadRequest();
        }

        if (envelope is null ||
            envelope.SchemaVersion != 2 ||
            string.IsNullOrWhiteSpace(envelope.Id) ||
            string.IsNullOrWhiteSpace(envelope.Category))
        {
            return BadRequest();
        }

        var resourceKey = "notification:" + envelope.Id;
        var (item, _) = await store.OpenAsync(
            new InboxOpenRequest
            {
                Kind = InboxKinds.Notification,
                Severity = InboxSeverities.IsKnown(envelope.Severity) ? envelope.Severity : InboxSeverities.Info,
                Subject = new InboxSubject
                {
                    NotificationId = envelope.Id,
                    StepId = envelope.Attributes?.GetValueOrDefault("stepId"),
                    ResourceKey = resourceKey
                },
                OpenedSeq = 0,
                CreatedAtUtc = envelope.OccurredAtUtc == default ? null : envelope.OccurredAtUtc
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Json(item, CoreJsonContext.Default.InboxItem);
    }

    internal static async Task<IResult> RebuildAsync(HttpContext ctx, CancellationToken cancellationToken)
    {
        var projector = ResolveProjector(ctx);
        if (projector is null)
            return Disabled();

        var response = await projector.RebuildAsync(cancellationToken).ConfigureAwait(false);
        return Results.Json(response, CoreJsonContext.Default.InboxRebuildResponse);
    }

    private static IResult BadRequest()
        => Results.Json(
            new OperationStatusResponse { Success = false, Error = InboxErrorCodes.BadRequest },
            CoreJsonContext.Default.OperationStatusResponse,
            statusCode: StatusCodes.Status400BadRequest);

    private static long? ParseLastEventId(string? value)
        => long.TryParse(value, out var seq) && seq >= 0 ? seq : null;

    private static async Task WriteRawAsync(HttpContext ctx, string frame, CancellationToken cancellationToken)
    {
        await ctx.Response.WriteAsync(frame, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
