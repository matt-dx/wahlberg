namespace Wahlberg.Services;

/// <summary>
/// Set once at startup by <c>Platforms/Windows/App.xaml.cs</c> when launched with
/// <c>--serve</c>. UI that depends on a native window (file pickers, external editor,
/// PDF/Mermaid export) checks this to fall back to a browser-safe alternative or hide itself.
/// </summary>
public static class AppMode
{
    public static bool IsServiceMode { get; set; }
}
