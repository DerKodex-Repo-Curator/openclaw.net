namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    /// <summary>
    /// Operator face for the skill index (design §7.2). Reindex changes what every subsequent run
    /// sees, so it is CSRF-protected and requires the admin role (<c>admin.skills*</c>).
    /// </summary>
    private static void MapSkillIndexEndpoints(WebApplication app, AdminEndpointServices services)
    {
        app.MapPost("/admin/skills/reindex", async (HttpContext ctx) =>
        {
            var (_, failure) = AuthorizeOperator(
                ctx,
                services.Startup,
                services.BrowserSessions,
                services.Operations,
                requireCsrf: true,
                endpointScope: "admin.skills.reindex");
            if (failure is not null)
            {
                await failure.ExecuteAsync(ctx);
                return;
            }

            await SkillIndexHandlers.ReindexAsync(ctx);
        });
    }
}
