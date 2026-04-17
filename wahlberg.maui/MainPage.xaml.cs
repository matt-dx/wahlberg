using Wahlberg.Services;

namespace Wahlberg;

public partial class MainPage : ContentPage
{
    private static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdown", ".mkd", ".mkdn"];

    public MainPage()
    {
        InitializeComponent();

        blazorWebView.BlazorWebViewInitialized += (_, args) =>
        {
#if WINDOWS
            var webview = args.WebView.CoreWebView2;

            // Enable the default browser context menu (right-click copy, etc.)
            webview.Settings.AreDefaultContextMenusEnabled = true;

            // Map local drives so relative image paths resolved by TabService can be loaded.
            // TabService rewrites <img src="relative"> to https://localfile-{drive}/path/to/file
            foreach (var driveInfo in DriveInfo.GetDrives())
            {
                if (!driveInfo.IsReady) continue;
                var letter = char.ToLowerInvariant(driveInfo.Name[0]);
                webview.SetVirtualHostNameToFolderMapping(
                    $"localfile-{letter}",
                    driveInfo.RootDirectory.FullName,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            }
#endif
        };
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnDrop(object? sender, DropEventArgs e)
    {
        try
        {
            await HandleDropAsync(e);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Drop error: {ex.Message}");
        }
    }

    private async Task HandleDropAsync(DropEventArgs e)
    {
        // Try platform-specific path extraction for WinUI
        var filePaths = GetDroppedFilePaths(e);
        if (filePaths.Count == 0) return;

        var tabService = Handler?.MauiContext?.Services.GetService<TabService>();
        if (tabService is null) return;

        foreach (var path in filePaths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(MarkdownExtensions, ext) < 0) continue;

            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path);
                MainThread.BeginInvokeOnMainThread(() => tabService.AddDocument(path, content));
            }
        }
    }

    private static List<string> GetDroppedFilePaths(DropEventArgs e)
    {
        var paths = new List<string>();

#if WINDOWS
        try
        {
            if (e.PlatformArgs is not null)
            {
                var dragArgs = e.PlatformArgs.DragEventArgs;
                if (dragArgs?.DataView is not null)
                {
                    var items = dragArgs.DataView.GetStorageItemsAsync().AsTask().GetAwaiter().GetResult();
                    foreach (var item in items)
                    {
                        if (item is Windows.Storage.StorageFile file)
                        {
                            paths.Add(file.Path);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Platform drop error: {ex.Message}");
        }
#endif

        return paths;
    }
}
