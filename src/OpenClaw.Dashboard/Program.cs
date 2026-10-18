using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using OpenClaw.Dashboard;
using OpenClaw.Dashboard.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();
builder.Services.AddLocalizationInterceptor<DashboardMudLocalizationInterceptor>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<LocalizationService>();
builder.Services.AddScoped<ApiService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<EventStreamService>();
// B3 run view: a separate client because it needs `id:` frames and the `terminal` frame.
builder.Services.AddScoped<RunEventStream>();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();

internal sealed class DashboardMudLocalizationInterceptor : ILocalizationInterceptor
{
    public LocalizedString Handle(string key, params object[] arguments)
    {
        return new LocalizedString(key, key, resourceNotFound: true);
    }
}
