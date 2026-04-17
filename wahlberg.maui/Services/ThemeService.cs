using System.Text.Json;
using Wahlberg.Models;

namespace Wahlberg.Services;

public class ThemeService
{
    private readonly string _themesDir;
    private readonly string _settingsPath;
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public List<ViewerTheme> Themes { get; private set; } = [];
    public ViewerTheme ActiveTheme { get; private set; } = new();

    public event Action? ThemeChanged;

    public ThemeService()
    {
        _themesDir = Path.Combine(FileSystem.AppDataDirectory, "themes");
        _settingsPath = Path.Combine(FileSystem.AppDataDirectory, "settings.json");
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        Directory.CreateDirectory(_themesDir);
        var files = Directory.GetFiles(_themesDir, "*.json");

        if (files.Length == 0)
        {
            var defaultTheme = new ViewerTheme { Name = "default" };
            await SaveThemeAsync(defaultTheme);
            Themes.Add(defaultTheme);
            ActiveTheme = defaultTheme;
        }
        else
        {
            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var theme = JsonSerializer.Deserialize<ViewerTheme>(json, JsonOptions);
                    if (theme is not null)
                        Themes.Add(theme);
                }
                catch { /* skip invalid files */ }
            }

            var activeThemeName = await LoadActiveThemeNameAsync();
            ActiveTheme = Themes.FirstOrDefault(t => t.Name == activeThemeName)
                          ?? Themes.FirstOrDefault()
                          ?? new ViewerTheme();
        }
    }

    public async Task SaveThemeAsync(ViewerTheme theme)
    {
        var path = Path.Combine(_themesDir, $"{SanitizeFileName(theme.Name)}.json");
        var json = JsonSerializer.Serialize(theme, JsonOptions);
        await File.WriteAllTextAsync(path, json);

        var existingIndex = Themes.FindIndex(t => t.Name == theme.Name);
        if (existingIndex >= 0)
            Themes[existingIndex] = theme;
        else
            Themes.Add(theme);
    }

    public async Task SetActiveThemeAsync(ViewerTheme theme)
    {
        ActiveTheme = theme;
        await SaveSettingsAsync();
        ThemeChanged?.Invoke();
    }

    public async Task DeleteThemeAsync(ViewerTheme theme)
    {
        if (theme.Name == "default") return;

        var path = Path.Combine(_themesDir, $"{SanitizeFileName(theme.Name)}.json");
        if (File.Exists(path))
            File.Delete(path);

        Themes.Remove(theme);

        if (ActiveTheme.Name == theme.Name)
        {
            ActiveTheme = Themes.FirstOrDefault() ?? new ViewerTheme();
            await SaveSettingsAsync();
            ThemeChanged?.Invoke();
        }
    }

    public string ExportThemeJson(ViewerTheme theme) =>
        JsonSerializer.Serialize(theme, JsonOptions);

    public ViewerTheme? ImportThemeJson(string json)
    {
        try { return JsonSerializer.Deserialize<ViewerTheme>(json, JsonOptions); }
        catch { return null; }
    }

    private async Task<string> LoadActiveThemeNameAsync()
    {
        if (!File.Exists(_settingsPath)) return "default";
        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath);
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            return settings?.GetValueOrDefault("activeTheme", "default") ?? "default";
        }
        catch { return "default"; }
    }

    private async Task SaveSettingsAsync()
    {
        var settings = new Dictionary<string, string> { ["activeTheme"] = ActiveTheme.Name };
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json);
    }

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
}
