using OpenClaw.Core.Skills;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class IntegrationEndpoints
{
    /// <summary>
    /// Machine-face skill index (<c>GET /api/integration/skill-index</c>): read-only, scope
    /// <c>integration.read</c>. Fails only on authentication, never on a bad index - the payload
    /// carries <c>ready</c>/<c>state</c> so callers can gate their own deploys with it.
    /// </summary>
    private static void MapSkillIndexEndpoint(
        RouteGroupBuilder group,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        BrowserSessionAuthService browserSessions)
    {
        group.MapGet("/skill-index", (HttpContext ctx) =>
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

            return SkillIndexHandlers.GetIndex(ctx, startup.Config.Skills);
        });
    }

    /// <summary>Stamps the index state header on a meta-invocation response.</summary>
    internal static void AnnotateSkillIndex(HttpContext ctx) => SkillIndexHandlers.Annotate(ctx);
}
