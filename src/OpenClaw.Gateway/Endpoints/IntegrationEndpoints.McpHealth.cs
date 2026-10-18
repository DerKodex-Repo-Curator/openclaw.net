using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class IntegrationEndpoints
{
    /// <summary>
    /// Machine-face MCP health (<c>GET /api/integration/mcp-health</c> and its
    /// <c>/stream</c> sibling): read-only, scope <c>integration.read</c>.
    /// </summary>
    /// <remarks>
    /// One face only, unlike the run surface: an MCP server's health is operational data, and the
    /// dashboard reads the same endpoint through its integration credentials. Mounting a second
    /// copy under <c>/admin</c> would be a second contract to keep in sync for no new reader.
    /// </remarks>
    private static void MapMcpHealthEndpoints(
        RouteGroupBuilder group,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        BrowserSessionAuthService browserSessions)
    {
        group.MapGet("/mcp-health", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(
                ctx,
                startup,
                runtime,
                browserSessions,
                endpointScope: "integration.read",
                requireCsrf: false);
            if (failure is not null)
                return failure;

            return McpHealthHandlers.GetHealth(ctx);
        });

        group.MapGet("/mcp-health/stream", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(
                ctx,
                startup,
                runtime,
                browserSessions,
                endpointScope: "integration.read",
                requireCsrf: false);
            if (failure is not null)
            {
                await failure.ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            await McpHealthHandlers.StreamAsync(ctx).ConfigureAwait(false);
        });
    }

    /// <summary>Stamps the degraded/down header on a meta-invocation response.</summary>
    internal static void AnnotateMcpHealth(HttpContext ctx) => McpHealthHandlers.Annotate(ctx);
}
