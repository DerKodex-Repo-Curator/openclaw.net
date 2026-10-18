using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Inbox;

namespace OpenClaw.Gateway.Composition;

/// <summary>
/// B7 inbox wiring: the stream hub, the projection queue, the store and the projector.
/// </summary>
/// <remarks>
/// <para>
/// The queue is registered as a leaf and injected into <see cref="MetaInvocationStore"/> as
/// <see cref="IInboxRunEventSink"/>, which is what keeps the dependency graph acyclic: the ledger
/// knows how to hand off an event, and the projector is the only thing that knows how to interpret
/// one. Handing the ledger the projector directly would make each depend on the other.
/// </para>
/// <para>
/// Everything is skipped when <c>OpenClaw:Inbox:Enabled</c> is false, so the handlers answer
/// <c>inbox.disabled</c> and nothing is written to disk.
/// </para>
/// </remarks>
internal static class InboxServicesExtensions
{
    public static IServiceCollection AddOpenClawInboxServices(
        this IServiceCollection services,
        GatewayStartupContext startup)
    {
        ArgumentNullException.ThrowIfNull(startup);
        var config = startup.Config.Inbox.Normalize();
        if (!config.Enabled)
            return services;

        services.AddSingleton(config);
        services.AddSingleton(sp => new InboxStream());
        services.AddSingleton<InboxProjectionQueue>();
        services.AddSingleton<IInboxRunEventSink>(sp => sp.GetRequiredService<InboxProjectionQueue>());
        services.AddSingleton(sp => new InboxStore(
            config,
            startup.Config.Memory.StoragePath,
            sp.GetRequiredService<InboxStream>(),
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<ILogger<InboxStore>>()));
        services.AddSingleton(sp => new InboxProjector(
            sp.GetRequiredService<InboxStore>(),
            sp.GetRequiredService<InboxProjectionQueue>(),
            config,
            sp.GetService<McpServerHealthStore>(),
            sp.GetService<SkillIndexCompiler>(),
            sp.GetRequiredService<MetaInvocationStore>().Events,
            sp.GetRequiredService<ILogger<InboxProjector>>(),
            sp.GetService<TimeProvider>()));
        services.AddHostedService(sp => sp.GetRequiredService<InboxProjector>());

        return services;
    }
}
