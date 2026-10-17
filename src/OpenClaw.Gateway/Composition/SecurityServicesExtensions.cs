using Microsoft.AspNetCore.Authentication.JwtBearer;
using OpenClaw.Core.ExternalCli;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Approvals;
using OpenClaw.Gateway.Security;

namespace OpenClaw.Gateway.Composition;

internal static class SecurityServicesExtensions
{
    public static IServiceCollection AddOpenClawSecurityServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        services.AddSingleton(sp => new MetaInvocationStore(
            startup.Config.Memory.StoragePath,
            startup.Config.MetaInvocations.RetentionDays));
        services.AddHostedService(sp => sp.GetRequiredService<MetaInvocationStore>());
        // The meta approval channel is opt-in: without the switch a gated tool is auto-denied.
        if (startup.Config.Integration.MetaApprovals.Enabled)
        {
            services.AddSingleton(sp =>
            {
                var options = startup.Config.Integration.MetaApprovals;
                return new MetaApprovalStore(
                    new MetaApprovalOptions
                    {
                        Enabled = options.Enabled,
                        Timeout = options.Timeout,
                        GrantTtl = options.GrantTtl,
                        StoragePath = options.StoragePath
                    },
                    startup.Config.Memory.StoragePath,
                    sp.GetRequiredService<ILogger<MetaApprovalStore>>());
            });
            services.AddSingleton<OpenClaw.Core.Approvals.IMetaApprovalStore>(
                sp => sp.GetRequiredService<MetaApprovalStore>());
            services.AddHostedService(sp => sp.GetRequiredService<MetaApprovalStore>());
        }

        services.AddSingleton(sp => new DeviceEnrollmentService(sp.GetRequiredService<OperatorAccountService>(), TimeProvider.System));
        // Register OIDC/JWT Bearer authentication when an OIDC Authority is configured,
        // regardless of AuthMode. This allows JWT tokens (e.g. from the web chat's
        // OIDC login) to be validated even when AuthMode is "token".
        // Require non-blank Authority even when IsOidcMode is true; fail fast otherwise.
        var hasOidcAuthority = !string.IsNullOrWhiteSpace(startup.Config.Security.Oidc.Authority);
        if (hasOidcAuthority)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = startup.Config.Security.Oidc.Authority;
                    options.Audience = startup.Config.Security.Oidc.Audience;
                    options.RequireHttpsMetadata = startup.Config.Security.Oidc.RequireHttpsMetadata;
                });
            services.AddAuthorization();
        }

        services.AddHttpClient<IMcpDelegatedCredentialProvider, McpDelegatedCredentialProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<ToolApprovalService>();
        services.AddSingleton(sp =>
            new PairingManager(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<PairingManager>>()));
        services.AddSingleton(sp => new BrowserSessionAuthService(startup.Config, sp.GetRequiredService<OperatorAccountService>()));
        services.AddSingleton(sp =>
            new OperatorAccountService(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<OperatorAccountService>>()));
        services.AddSingleton(sp =>
            new OrganizationPolicyService(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<OrganizationPolicyService>>()));
        services.AddSingleton(sp =>
            new AdminSettingsService(
                startup.Config,
                startup.BaseAdminSettings ?? AdminSettingsService.CreateSnapshot(startup.Config),
                AdminSettingsService.GetSettingsPath(startup.Config),
                sp.GetRequiredService<ILogger<AdminSettingsService>>()));
        services.AddSingleton(sp =>
            new PluginAdminSettingsService(
                startup.Config,
                sp.GetRequiredService<ILogger<PluginAdminSettingsService>>()));
        services.AddSingleton(sp =>
            new ApprovalAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ApprovalAuditStore>>()));
        services.AddSingleton(sp =>
            new RuntimeEventStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<RuntimeEventStore>>(),
                sp.GetRequiredService<OpenClaw.Core.Observability.RuntimeMetrics>()));
        services.AddSingleton(sp =>
            new ExternalCliAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ExternalCliAuditStore>>()));
        services.AddSingleton<IExternalCliAuditSink>(sp => sp.GetRequiredService<ExternalCliAuditStore>());
        services.AddSingleton<IExternalCliEventSink>(sp =>
            new ExternalCliRuntimeEventSink(sp.GetRequiredService<RuntimeEventStore>()));
        services.AddSingleton(sp =>
            new OperatorAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<OperatorAuditStore>>(),
                sp.GetRequiredService<OpenClaw.Core.Observability.RuntimeMetrics>()));
        services.AddSingleton(sp =>
            new ToolApprovalGrantStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ToolApprovalGrantStore>>()));
        services.AddSingleton(sp =>
            new WebhookDeliveryStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<WebhookDeliveryStore>>()));
        services.AddSingleton(sp =>
            new PluginHealthService(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<PluginHealthService>>(),
                startup.Config.Plugins));
        services.AddSingleton(sp =>
            new ContractStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ContractStore>>()));
        services.AddSingleton(sp =>
            new ContractGovernanceService(
                startup,
                sp.GetRequiredService<ContractStore>(),
                sp.GetRequiredService<RuntimeEventStore>(),
                sp.GetRequiredService<OpenClaw.Core.Observability.ProviderUsageTracker>(),
                sp.GetRequiredService<ILogger<ContractGovernanceService>>()));

        return services;
    }
}
