using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Models;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Endpoints;

/// <summary>Who is asking, and whether they may see every run or only their own.</summary>
internal readonly record struct MetaRunCaller(string? CallerId, bool Unrestricted);

/// <summary>
/// B3 read surface: the run index, the event page, and the event stream.
/// </summary>
/// <remarks>
/// <para>
/// The same three handlers are mounted twice — on <c>/api/integration/meta-invocations*</c> for
/// machines and on <c>/admin/meta-invocations*</c> for the dashboard (design §6.5.1). They are one
/// implementation with two authorization delegates, not two copies: the DTOs, the error codes and
/// the <c>seq</c>/<c>gap</c>/<c>synthetic</c> semantics cannot drift between the two faces.
/// </para>
/// <para>
/// All three are GET. There is deliberately no resume, cancel or approve here: a run view that can
/// mutate is a run view that gets clicked by mistake at 3am.
/// </para>
/// </remarks>
internal static class MetaRunEventEndpoints
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;
    public const int DefaultListLimit = 50;
    public const int MaxListLimit = 200;

    /// <summary>How often the stream looks for new events. The dashboard is the only consumer.</summary>
    private const int StreamPollMilliseconds = 200;

    /// <summary>The ledger is only consulted for terminal detection, once per second.</summary>
    private const int StreamStatusPollMilliseconds = 1000;

    public const string AfterInvalid = "events.after_invalid";
    public const string InvocationNotFound = "invocation.not_found";
    public const string InvocationForbidden = "invocation.forbidden";
    public const string CursorInvalid = "runs.cursor_invalid";
    public const string InvocationsDisabled = "invocations.disabled";

    public static void MapMetaInvocationEndpoints(
        IEndpointRouteBuilder routes,
        string prefix,
        Func<HttpContext, (MetaRunCaller Caller, IResult? Failure)> authorize)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(authorize);

        routes.MapGet($"{prefix}", async (HttpContext ctx) =>
        {
            var (caller, failure) = authorize(ctx);
            if (failure is not null)
                return failure;

            var store = ResolveStore(ctx);
            if (store is null)
                return MetaInvocationsDisabled();

            if (!TryReadListQuery(ctx, out var query, out var badQuery))
                return badQuery!;

            var listQuery = query!.Value;
            var (items, hasMore) = await store.ListAsync(
                caller.Unrestricted ? null : caller.CallerId,
                listQuery.SessionId,
                listQuery.Status,
                listQuery.Limit,
                listQuery.CursorCreatedAtUtc,
                listQuery.CursorInvocationId,
                ctx.RequestAborted);

            var summaries = items.Select(ToSummary).ToList();
            var nextCursor = hasMore && items.Count > 0
                ? EncodeCursor(items[^1].CreatedAtUtc, items[^1].InvocationId)
                : null;

            return Results.Json(
                new MetaInvocationListResponse(summaries, nextCursor),
                CoreJsonContext.Default.MetaInvocationListResponse);
        });

        routes.MapGet($"{prefix}/{{invocationId:guid}}/events", async (HttpContext ctx, Guid invocationId) =>
        {
            var (caller, failure) = authorize(ctx);
            if (failure is not null)
                return failure;

            var store = ResolveStore(ctx);
            if (store is null)
                return MetaInvocationsDisabled();

            if (!TryReadAfter(ctx.Request.Query["after"].ToString(), out var after))
                return BadRequest(AfterInvalid);

            var limit = ReadLimit(ctx.Request.Query["limit"].ToString());
            var (page, pageFailure) = await BuildPageAsync(
                store, invocationId, caller, after, limit, ctx.RequestServices, ctx.RequestAborted);
            if (pageFailure is not null)
                return pageFailure;

            return Results.Json(page, CoreJsonContext.Default.MetaRunEventPage);
        });

        routes.MapGet($"{prefix}/{{invocationId:guid}}/events/stream", async (HttpContext ctx, Guid invocationId) =>
        {
            var (caller, failure) = authorize(ctx);
            if (failure is not null)
                return failure;

            var store = ResolveStore(ctx);
            if (store is null)
                return MetaInvocationsDisabled();

            // Last-Event-ID wins over `after`: a reconnecting browser sends the former, a polling
            // client the latter, and mixing them must not replay from the older cursor.
            var lastEventId = ctx.Request.Headers["Last-Event-ID"].ToString();
            var cursorText = string.IsNullOrWhiteSpace(lastEventId)
                ? ctx.Request.Query["after"].ToString()
                : lastEventId;
            if (!TryReadAfter(cursorText, out var after))
                return BadRequest(AfterInvalid);

            var record = await store.TryGetByInvocationIdAsync(invocationId, ctx.RequestAborted);
            var (_, recordFailure) = CheckOwnership(record, caller);
            if (recordFailure is not null)
                return recordFailure;

            return await StreamAsync(ctx, store, invocationId, after);
        });
    }

    private static async Task<IResult> StreamAsync(
        HttpContext ctx,
        MetaInvocationStore store,
        Guid invocationId,
        long after)
    {
        var cancellationToken = ctx.RequestAborted;
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        var cursor = after;
        var nextStatusCheck = Environment.TickCount64;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var events = store.Events.Read(invocationId).Events;
                foreach (var runEvent in events)
                {
                    if (runEvent.Seq <= cursor)
                        continue;

                    await WriteSseAsync(
                        ctx,
                        "run_event",
                        runEvent.Seq.ToString(CultureInfo.InvariantCulture),
                        JsonSerializer.Serialize(runEvent, CoreJsonContext.Default.MetaRunEvent),
                        cancellationToken);
                    cursor = runEvent.Seq;
                }

                if (Environment.TickCount64 >= nextStatusCheck)
                {
                    nextStatusCheck = Environment.TickCount64 + StreamStatusPollMilliseconds;
                    var record = await store.TryGetByInvocationIdAsync(invocationId, cancellationToken);
                    if (record is null)
                        return Results.Empty;

                    if (record.Status is MetaInvocationStatus.Completed or MetaInvocationStatus.Uncertain)
                    {
                        // Everything up to the terminal event has been written above, so the client
                        // can stop reconnecting and treat its timeline as complete.
                        var terminal = new MetaRunEventStreamTerminal(
                            invocationId,
                            record.Status.ToString(),
                            cursor);
                        await WriteSseAsync(
                            ctx,
                            "terminal",
                            id: null,
                            JsonSerializer.Serialize(terminal, CoreJsonContext.Default.MetaRunEventStreamTerminal),
                            cancellationToken);
                        return Results.Empty;
                    }
                }

                await Task.Delay(StreamPollMilliseconds, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client went away: the normal end of an SSE request.
        }
        catch (IOException)
        {
            // Broken pipe: same story, and there is nobody left to tell.
        }

        return Results.Empty;
    }

    private static async Task WriteSseAsync(
        HttpContext ctx,
        string eventName,
        string? id,
        string data,
        CancellationToken cancellationToken)
    {
        var frame = new StringBuilder(data.Length + 64)
            .Append("event: ").Append(eventName).Append('\n');
        if (!string.IsNullOrEmpty(id))
            frame.Append("id: ").Append(id).Append('\n');
        frame.Append("data: ").Append(data).Append("\n\n");

        await ctx.Response.WriteAsync(frame.ToString(), cancellationToken);
        await ctx.Response.Body.FlushAsync(cancellationToken);
    }

    private static async Task<(MetaRunEventPage? Page, IResult? Failure)> BuildPageAsync(
        MetaInvocationStore store,
        Guid invocationId,
        MetaRunCaller caller,
        long after,
        int limit,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var record = await store.TryGetByInvocationIdAsync(invocationId, cancellationToken);
        var (current, failure) = CheckOwnership(record, caller);
        if (failure is not null)
            return (null, failure);

        var invocation = current!;
        var read = store.Events.Read(invocationId);
        var events = read.Events;
        var synthetic = false;

        if (events.Count == 0 && invocation.Status != MetaInvocationStatus.Running)
        {
            // No journal: either the run predates B3 or the journal was lost. A coarse timeline from
            // the run record is far more useful than an empty list, as long as it says it is derived.
            var session = await TryLoadSessionAsync(invocation.SessionId, services, cancellationToken);
            events = MetaRunEventComposer.FromHistory(invocation, session);
            synthetic = events.Count > 0;
        }

        var nextSeq = events.Count == 0 ? 0 : events[^1].Seq;
        if (after > nextSeq)
            return (null, BadRequest(AfterInvalid));

        var page = events
            .Where(runEvent => runEvent.Seq > after)
            .Take(limit)
            .ToList();

        return (
            new MetaRunEventPage(
                invocationId,
                page,
                nextSeq,
                invocation.Status is MetaInvocationStatus.Completed or MetaInvocationStatus.Uncertain,
                invocation.EventsGap || read.UnreadableLines > 0,
                synthetic),
            null);
    }

    private static (MetaInvocationRecord? Record, IResult? Failure) CheckOwnership(
        MetaInvocationRecord? record,
        MetaRunCaller caller)
    {
        if (record is null)
            return (null, NotFound());

        if (!caller.Unrestricted &&
            !string.Equals(record.CallerId, caller.CallerId, StringComparison.Ordinal))
        {
            // Deliberately 403 rather than 404: the caller owns the id, the caller just may not read
            // someone else's run. Hiding existence is not worth a misleading error here.
            return (null, Status(StatusCodes.Status403Forbidden, InvocationForbidden));
        }

        return (record, null);
    }

    private static async Task<Session?> TryLoadSessionAsync(
        string? sessionId,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var sessions = services.GetService<SessionManager>();
        if (sessions is null)
            return null;

        try
        {
            return await sessions.LoadAsync(sessionId, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            // A broken session must not take the run timeline down with it.
            return null;
        }
    }

    private static MetaInvocationSummary ToSummary(MetaInvocationRecord record)
        => new(
            record.InvocationId,
            record.SessionId,
            record.Skill,
            record.Status.ToString(),
            record.CreatedAtUtc,
            record.Pause?.Reason,
            record.Pause?.StepId,
            record.Error);

    private static MetaInvocationStore? ResolveStore(HttpContext ctx)
        => ctx.RequestServices.GetService<MetaInvocationStore>();

    private static bool TryReadAfter(string? value, out long after)
    {
        after = 0;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out after))
            return false;

        return after >= 0;
    }

    private static int ReadLimit(string? value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit <= 0)
            return DefaultLimit;

        return Math.Min(limit, MaxLimit);
    }

    private readonly record struct ListQuery(
        string? SessionId,
        MetaInvocationStatus? Status,
        int Limit,
        DateTimeOffset? CursorCreatedAtUtc,
        Guid? CursorInvocationId);

    private static bool TryReadListQuery(HttpContext ctx, out ListQuery? query, out IResult? failure)
    {
        query = null;
        failure = null;

        MetaInvocationStatus? status = null;
        var statusText = ctx.Request.Query["status"].ToString();
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            if (!Enum.TryParse<MetaInvocationStatus>(statusText, ignoreCase: true, out var parsed))
            {
                failure = BadRequest(CursorInvalid);
                return false;
            }

            status = parsed;
        }

        var limit = DefaultListLimit;
        var limitText = ctx.Request.Query["limit"].ToString();
        if (!string.IsNullOrWhiteSpace(limitText))
        {
            if (!int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit <= 0)
            {
                failure = BadRequest(CursorInvalid);
                return false;
            }

            limit = Math.Min(limit, MaxListLimit);
        }

        DateTimeOffset? cursorCreated = null;
        Guid? cursorId = null;
        var cursor = ctx.Request.Query["cursor"].ToString();
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeCursor(cursor, out var created, out var id))
            {
                failure = BadRequest(CursorInvalid);
                return false;
            }

            cursorCreated = created;
            cursorId = id;
        }

        var sessionId = ctx.Request.Query["sessionId"].ToString();
        query = new ListQuery(
            string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            status,
            limit,
            cursorCreated,
            cursorId);
        return true;
    }

    private static string EncodeCursor(DateTimeOffset createdAtUtc, Guid invocationId)
    {
        var payload = $"{createdAtUtc.UtcTicks}|{invocationId:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecodeCursor(string value, out DateTimeOffset createdAtUtc, out Guid invocationId)
    {
        createdAtUtc = default;
        invocationId = Guid.Empty;
        if (value.Length > 256)
            return false;

        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - normalized.Length % 4) % 4;
        try
        {
            var bytes = Convert.FromBase64String(normalized + new string('=', padding));
            var text = Encoding.UTF8.GetString(bytes);
            var separator = text.IndexOf('|');
            if (separator <= 0)
                return false;

            if (!long.TryParse(text[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                return false;
            if (!Guid.TryParseExact(text[(separator + 1)..], "N", out invocationId))
                return false;

            createdAtUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IResult BadRequest(string code)
        => Status(StatusCodes.Status400BadRequest, code);

    private static IResult NotFound()
        => Status(StatusCodes.Status404NotFound, InvocationNotFound);

    private static IResult MetaInvocationsDisabled()
        => Status(StatusCodes.Status404NotFound, InvocationsDisabled);

    private static IResult Status(int statusCode, string code)
        => Results.Json(
            new OperationStatusResponse { Success = false, Error = code },
            CoreJsonContext.Default.OperationStatusResponse,
            statusCode: statusCode);
}
