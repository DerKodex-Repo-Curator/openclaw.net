using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

/// <summary>
/// Operator face for the inbox (design §10.3): the todo list, its four mutations, the change stream,
/// and the rebuild command.
/// </summary>
/// <remarks>
/// <para>
/// Mounted at <c>/api/inbox</c> rather than under <c>/admin</c> because that is what the design and
/// the dashboard both name, and because a todo list is read far more often than it is written: the
/// same group carries the read and the write scopes instead of duplicating the surface.
/// </para>
/// <para>
/// The rebuild is the exception - it is a maintenance command that rewrites the projection - so it
/// sits on the admin path, requires CSRF, and needs the operator role.
/// </para>
/// </remarks>
internal static class InboxEndpoints
{
    public static void MapOpenClawInboxEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var group = app.MapGroup("/api/inbox").WithTags("OpenClaw Inbox");

        group.MapGet("/", async (HttpContext ctx) =>
        {
            var failure = Authorize(
                ctx, startup, runtime, browserSessions, endpointScope: "inbox.read", requireCsrf: false);
            if (failure is not null)
                return failure;

            return await InboxHandlers.ListAsync(ctx, ctx.RequestAborted).ConfigureAwait(false);
        });

        group.MapGet("/stream", async (HttpContext ctx) =>
        {
            var failure = Authorize(
                ctx, startup, runtime, browserSessions, endpointScope: "inbox.read", requireCsrf: false);
            if (failure is not null)
            {
                await failure.ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            await InboxHandlers.StreamAsync(ctx).ConfigureAwait(false);
        });

        group.MapPost("/{id}/ack", async (HttpContext ctx, string id) =>
        {
            var failure = Authorize(
                ctx, startup, runtime, browserSessions, endpointScope: "inbox.mutate", requireCsrf: true);
            if (failure is not null)
                return failure;

            return await InboxHandlers.AcknowledgeAsync(ctx, id, ctx.RequestAborted).ConfigureAwait(false);
        });

        group.MapPost("/{id}/snooze", async (HttpContext ctx, string id) =>
        {
            var failure = Authorize(
                ctx, startup, runtime, browserSessions, endpointScope: "inbox.mutate", requireCsrf: true);
            if (failure is not null)
                return failure;

            return await InboxHandlers.SnoozeAsync(ctx, id, ctx.RequestAborted).ConfigureAwait(false);
        });

        group.MapPost("/{id}/resolve", async (HttpContext ctx, string id) =>
        {
            var failure = Authorize(
                ctx, startup, runtime, browserSessions, endpointScope: "inbox.mutate", requireCsrf: true);
            if (failure is not null)
                return failure;

            return await InboxHandlers.ResolveAsync(ctx, id, ctx.RequestAborted).ConfigureAwait(false);
        });

        app.MapPost("/api/admin/inbox/rebuild", async (HttpContext ctx) =>
        {
            var failure = Authorize(
                ctx, startup, runtime, browserSessions, endpointScope: "admin.inbox.rebuild", requireCsrf: true);
            if (failure is not null)
                return failure;

            return await InboxHandlers.RebuildAsync(ctx, ctx.RequestAborted).ConfigureAwait(false);
        }).WithTags("OpenClaw Inbox");
    }

    private static IResult? Authorize(
        HttpContext ctx,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        BrowserSessionAuthService browserSessions,
        string endpointScope,
        bool requireCsrf)
    {
        var (_, failure) = EndpointHelpers.AuthorizeOperatorEndpoint(
            ctx, startup, browserSessions, runtime.Operations, requireCsrf, endpointScope);

        return failure;
    }
}
