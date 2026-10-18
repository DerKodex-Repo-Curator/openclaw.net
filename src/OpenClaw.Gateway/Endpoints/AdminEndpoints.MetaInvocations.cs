using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    /// <summary>
    /// B3 run events, operator face (design §6.5.1). The handlers are the machine-face handlers with
    /// a different authorization delegate — not a second implementation — so the dashboard cannot
    /// end up reading a DTO shape the integration callers never see.
    /// </summary>
    /// <remarks>
    /// Scope <c>admin.meta-invocations.read</c> is not in the role table, so it falls through to
    /// <c>Viewer</c>: reading a run timeline is exactly the read a viewer may do. Nothing here is a
    /// mutation, and nothing here is mounted as anything but GET (T-B3-16).
    /// </remarks>
    private static void MapMetaInvocationEndpoints(WebApplication app, AdminEndpointServices services)
    {
        MetaRunEventEndpoints.MapMetaInvocationEndpoints(
            app,
            "/admin/meta-invocations",
            ctx =>
            {
                var (_, failure) = AuthorizeOperator(
                    ctx,
                    services.Startup,
                    services.BrowserSessions,
                    services.Operations,
                    requireCsrf: false,
                    endpointScope: "admin.meta-invocations.read");
                if (failure is not null)
                    return (default, failure);

                // Unrestricted: an operator opening the run view is auditing the whole node, and the
                // ledger's CallerId is an integration identity, not an operator identity.
                return (new MetaRunCaller(CallerId: null, Unrestricted: true), null);
            });
    }
}
