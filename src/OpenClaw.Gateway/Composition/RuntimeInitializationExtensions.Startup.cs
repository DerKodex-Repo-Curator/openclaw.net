using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway.Pipeline;
using OpenClaw.Gateway.Tools;

namespace OpenClaw.Gateway.Composition;

/// <summary>
/// Startup steps lifted out of <see cref="InitializeOpenClawRuntimeAsync"/> so that method stays a
/// readable sequence of decisions rather than a pile of details (see GatewayStructureTests' budget).
/// </summary>
internal static partial class RuntimeInitializationExtensions
{
    /// <summary>
    /// B4: one authoritative skill compile, performed before the runtime exists.
    /// </summary>
    /// <remarks>
    /// Everything downstream - the runtime factory, the artifact runtime, the probes and every
    /// endpoint - reads this same snapshot, so there is no window where the runtime has new skills
    /// and the index reports old ones (design §7.2). The hosted-service compile inside
    /// <see cref="SkillIndexCompiler"/> then sees <c>Version &gt; 0</c> and skips its own pass.
    /// Without a compiler (tests, embedded hosts) this falls back to parsing the skill tree directly.
    /// </remarks>
    private static async Task<(SkillIndexCompiler? Compiler, IReadOnlyList<SkillDefinition> Skills)>
        CompileStartupSkillsAsync(
            IServiceProvider services,
            GatewayConfig config,
            string? workspacePath,
            IReadOnlyList<string> pluginSkillRoots,
            SkillArtifactRuntime artifactRuntime,
            ILoggerFactory loggerFactory)
    {
        var compiler = services.GetService<SkillIndexCompiler>();
        if (compiler is null)
        {
            var direct = SkillLoader.LoadAll(
                config.Skills,
                workspacePath,
                loggerFactory.CreateLogger("SkillLoader"),
                pluginSkillRoots);
            artifactRuntime.ReplaceSkills(direct);
            return (null, direct);
        }

        compiler.Configure(workspacePath, pluginSkillRoots);
        var snapshot = await compiler.CompileAsync(SkillIndexCompileTrigger.Startup);
        artifactRuntime.ReplaceSkills(snapshot.Skills);
        return (compiler, snapshot.Skills);
    }

    /// <summary>
    /// Optional network surfaces that start last and must never block a working gateway: a failure
    /// here is logged by the service itself and does not abort startup.
    /// </summary>
    private static async Task StartAuxiliaryServicesAsync(
        WebApplication app,
        GatewayConfig config,
        ILoggerFactory loggerFactory,
        GatewayRuntimeShutdownCoordinator shutdownCoordinator)
    {
        if (config.Tailscale.Enabled)
        {
            var tailscale = new Integrations.TailscaleService(
                config.Tailscale,
                config.Port,
                loggerFactory.CreateLogger<Integrations.TailscaleService>());
            shutdownCoordinator.RegisterAsyncCleanup("tailscale serve/funnel", _ => tailscale.DisposeAsync());
            await tailscale.StartAsync(app.Lifetime.ApplicationStopping);
        }

        if (config.Mdns.Enabled)
        {
            var mdns = new Integrations.MdnsDiscoveryService(
                config.Mdns,
                config.Port,
                authRequired: !string.IsNullOrWhiteSpace(config.AuthToken),
                loggerFactory.CreateLogger<Integrations.MdnsDiscoveryService>());
            shutdownCoordinator.RegisterAsyncCleanup("mDNS discovery", _ => mdns.DisposeAsync());
            mdns.Start(app.Lifetime.ApplicationStopping);
        }
    }
}
