using Microsoft.AspNetCore.Builder;

namespace Wahlberg.WinUI;

/// <summary>
/// Hosts the existing Blazor UI (Routes.razor -> Home.razor etc.) over Kestrel/Blazor Server
/// instead of the native WinUI window, when launched with <c>--serve</c>. No MAUI Window is
/// ever created in this code path, so anything that depends on one (file pickers, PDF/Mermaid
/// export via HiddenWebView) must be guarded with <see cref="Services.AppMode.IsServiceMode"/>.
/// </summary>
public static class ServiceHost
{
    public static async Task RunAsync(int port)
    {
        // WebApplication.CreateBuilder() resolves ContentRoot/WebRoot relative to the current
        // working directory by default, which won't contain wwwroot if launched from elsewhere.
        // Anchor it to the exe's own directory instead, where the MAUI build already places wwwroot.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });

        MauiProgram.RegisterSharedServices(builder.Services);
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();

        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapRazorComponents<Wahlberg.Components.WebHost.App>()
            .AddInteractiveServerRenderMode();

        app.Urls.Add($"http://localhost:{port}");

        await app.RunAsync();
    }
}
