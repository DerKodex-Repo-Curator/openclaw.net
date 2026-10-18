using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class IntegrationEndpoints
{
    /// <summary>
    /// Machine-face inbox ingest (<c>POST /api/integration/inbox/items</c>): the receiving end of
    /// DrasiWake's <c>inbox</c> notification channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only endpoint in the whole gateway that turns a notification into a durable item,
    /// which is what keeps the boundary honest (design §10.4): a notification may be lost, an item
    /// may not, and "which categories are worth a todo" is decided by the sender's channel filter,
    /// not by a rule buried here.
    /// </para>
    /// <para>
    /// Scope is <c>integration.mutate.inbox</c> (operator role): the sender is a service, not a person,
    /// and it needs no read access to the list it is feeding.
    /// </para>
    /// </remarks>
    private static void MapInboxIngestEndpoint(
        RouteGroupBuilder group,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        BrowserSessionAuthService browserSessions)
    {
        group.MapPost("/inbox/items", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(
                ctx,
                startup,
                runtime,
                browserSessions,
                endpointScope: "integration.mutate.inbox",
                requireCsrf: false);
            if (failure is not null)
                return failure;

            return await InboxHandlers.IngestAsync(ctx, ctx.RequestAborted).ConfigureAwait(false);
        });
    }
}
