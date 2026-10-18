using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;

namespace OpenClaw.Gateway.Endpoints;

/// <summary>
/// The three skill-index handlers, written without any authorization of their own.
/// </summary>
/// <remarks>
/// Authorization stays in the two faces (<c>/api/integration/skill-index</c> and
/// <c>/admin/skills/reindex</c>) for the same reason B3 split its mounts: the handler is the only
/// thing that decides a status code or a payload, so a test can mount it directly and still be
/// exercising production code.
/// </remarks>
internal static class SkillIndexHandlers
{
    /// <summary>
    /// Response header that tells a caller which skill index served (or refused) its request.
    /// </summary>
    internal const string HeaderName = "X-OpenClaw-Skill-Index";

    internal static SkillIndexCompiler? Resolve(HttpContext ctx)
        => ctx.RequestServices.GetService<SkillIndexCompiler>();

    /// <summary>Machine-face payload. Never fails on a bad index; the body carries the verdict.</summary>
    internal static IResult GetIndex(HttpContext ctx, SkillsConfig? skillsConfig)
    {
        var snapshot = Resolve(ctx)?.Current ?? SkillIndexSnapshot.Empty;

        return Results.Json(
            SkillIndexProjection.ToResponse(snapshot, skillsConfig),
            CoreJsonContext.Default.SkillIndexResponse);
    }

    /// <summary>
    /// Forces a compile. Returns <c>previousVersion</c> next to <c>version</c> so a rejected attempt
    /// (which keeps the old index active) is visible instead of looking like a successful reindex.
    /// </summary>
    /// <remarks>
    /// Writes the response itself and returns a plain <see cref="Task"/> on purpose: a route lambda
    /// that returns <c>Task&lt;IResult&gt;</c> also converts to <see cref="RequestDelegate"/>, which
    /// silently discards the result and answers 200 with an empty body (ASP0016).
    /// </remarks>
    internal static async Task ReindexAsync(HttpContext ctx)
    {
        var result = await BuildReindexResultAsync(ctx).ConfigureAwait(false);
        await result.ExecuteAsync(ctx).ConfigureAwait(false);
    }

    internal static async Task<IResult> BuildReindexResultAsync(HttpContext ctx)
    {
        var compiler = Resolve(ctx);
        if (compiler is null)
        {
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = "Skill index compiler is not registered." },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var previousVersion = compiler.Current.Version;
        var snapshot = await compiler
            .CompileAsync(SkillIndexCompileTrigger.Manual, ctx.RequestAborted)
            .ConfigureAwait(false);

        return Results.Json(
            new SkillReindexResponse
            {
                Version = snapshot.Version,
                PreviousVersion = previousVersion,
                Fingerprint = snapshot.Fingerprint,
                Ready = snapshot.Ready,
                State = snapshot.StateName,
                Diagnostics = SkillIndexProjection.ToDiagnostics(snapshot.Diagnostics)
            },
            CoreJsonContext.Default.SkillReindexResponse);
    }

    /// <summary>
    /// Readiness payload and status code. Unready always fails the probe; degraded only fails it
    /// when the operator asked for that (<c>Skills.FailOnDegradedSkills</c>). Liveness is untouched.
    /// </summary>
    internal static IResult Readiness(HttpContext ctx, bool failOnDegraded)
    {
        var snapshot = Resolve(ctx)?.Current;
        var skills = snapshot is null ? null : SkillIndexProjection.ToHealth(snapshot);

        var notReady = snapshot is not null &&
                       (!snapshot.Ready ||
                        (failOnDegraded && snapshot.State == SkillIndexState.Degraded));

        return Results.Json(
            new HealthResponse
            {
                Status = notReady ? "not_ready" : "ready",
                Uptime = Environment.TickCount64,
                Skills = skills
            },
            CoreJsonContext.Default.HealthResponse,
            statusCode: notReady ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
    }

    /// <summary>
    /// Stamps <see cref="HeaderName"/> with the current index state. A caller that is allowed to run
    /// against a degraded index needs to know that is what happened (design §7.3, T-B4-8).
    /// </summary>
    internal static void Annotate(HttpContext ctx)
    {
        var snapshot = Resolve(ctx)?.Current;
        if (snapshot is null)
            return;

        ctx.Response.Headers[HeaderName] = snapshot.StateName;
    }
}
