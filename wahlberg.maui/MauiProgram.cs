using CommunityToolkit.Maui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wahlberg;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		RegisterSharedServices(builder.Services);

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

	/// <summary>
	/// Service registrations shared between the native MAUI app and the
	/// Windows-only Kestrel/Blazor Server host (<c>--serve</c> mode), so both
	/// hosting models build identical service graphs.
	/// </summary>
	internal static void RegisterSharedServices(IServiceCollection services)
	{
		services.AddSingleton<Services.TabService>();
		services.AddSingleton<Services.ThemeService>();
		services.AddSingleton<Services.EditorService>();
		services.AddSingleton<Services.ExportService>();
		services.AddSingleton<Services.DiffService>();
	}
}
