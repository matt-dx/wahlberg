using System.Diagnostics;
using System.Text.Json;

namespace Wahlberg.Services;

public class EditorService
{
    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string EditorPath { get; private set; } = string.Empty;
    public event Action? StateChanged;

    public EditorService()
    {
        _settingsPath = Path.Combine(FileSystem.AppDataDirectory, "editor.json");
    }

    public async Task InitializeAsync()
    {
        if (!File.Exists(_settingsPath)) return;
        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath);
            var settings = JsonSerializer.Deserialize<EditorSettings>(json, JsonOptions);
            EditorPath = settings?.EditorPath ?? string.Empty;
        }
        catch { /* corrupt file — start fresh */ }
    }

    public async Task SetEditorPathAsync(string path)
    {
        EditorPath = path;
        var json = JsonSerializer.Serialize(new EditorSettings { EditorPath = path }, JsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json);
        StateChanged?.Invoke();
    }

    public async Task ClearEditorAsync() => await SetEditorPathAsync(string.Empty);

    public void OpenFile(string filePath)
    {
        if (string.IsNullOrEmpty(EditorPath) || !File.Exists(EditorPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = EditorPath,
                Arguments = $"\"{filePath}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error opening editor: {ex.Message}");
        }
    }

    public static (string Icon, string Name) GetEditorInfo(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return ("bi-pencil-square", "None");
        var stem = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        return stem switch
        {
            "code" or "code - insiders" or "code-insiders" => ("devicon-vscode-plain", "VS Code"),
            "sublime_text" => ("devicon-sublimetext-plain", "Sublime Text"),
            "atom" => ("devicon-atom-plain", "Atom"),
            "vim" or "gvim" => ("devicon-vim-plain", "Vim"),
            "nvim" => ("devicon-neovim-plain", "Neovim"),
            _ => ("bi bi-pencil-square", Path.GetFileNameWithoutExtension(exePath))
        };
    }
}

file class EditorSettings
{
    public string EditorPath { get; set; } = string.Empty;
}
