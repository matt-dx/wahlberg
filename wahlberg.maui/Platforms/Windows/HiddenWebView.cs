using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinRT.Interop;

namespace Wahlberg.Platforms.Windows;

// Hosts a WebView2 off-screen (in its own hidden Window, via AppWindow.Hide()) so export
// features can drive a real browser engine — for printing to PDF or rendering Mermaid
// diagrams — without disturbing the visible viewer or depending on MAUI's visual tree shape.
// Must be created and used on the UI thread (MainThread.BeginInvokeOnMainThread).
internal sealed class HiddenWebView : IAsyncDisposable
{
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(20);

    public WebView2 View { get; }
    public CoreWebView2Environment Environment { get; }

    private readonly Microsoft.UI.Xaml.Window _window;
    private readonly string _userDataFolder;

    private HiddenWebView(Microsoft.UI.Xaml.Window window, WebView2 view, CoreWebView2Environment environment, string userDataFolder)
    {
        _window = window;
        View = view;
        Environment = environment;
        _userDataFolder = userDataFolder;
    }

    public static async Task<HiddenWebView> CreateAsync()
    {
        var window = new Microsoft.UI.Xaml.Window();
        var webview = new WebView2();
        window.Content = webview;

        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(1024, 768));
        appWindow.Hide();

        // Each hidden instance gets its own profile folder — reusing one across
        // overlapping/successive instances can block on the previous instance's
        // browser-process shutdown still holding the profile lock.
        var userDataFolder = Path.Combine(FileSystem.CacheDirectory, $"wv2-{Guid.NewGuid()}");
        var env = await WithTimeout(
            CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, new CoreWebView2EnvironmentOptions()).AsTask(),
            "Creating the WebView2 environment");
        await WithTimeout(webview.EnsureCoreWebView2Async(env).AsTask(), "Initializing WebView2");

        return new HiddenWebView(window, webview, env, userDataFolder);
    }

    public async Task NavigateAndWaitAsync(string url)
    {
        var tcs = new TaskCompletionSource<bool>();
        void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) tcs.TrySetResult(true);
            else tcs.TrySetException(new InvalidOperationException($"Navigation failed: {args.WebErrorStatus}"));
        }

        View.NavigationCompleted += OnNavigationCompleted;
        try
        {
            View.CoreWebView2.Navigate(url);
            await WithTimeout(tcs.Task, "Navigating");
        }
        finally
        {
            View.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    private static async Task WithTimeout(Task task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(SetupTimeout)) != task)
            throw new TimeoutException($"{what} timed out.");
        await task; // observe/rethrow any exception
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(SetupTimeout)) != task)
            throw new TimeoutException($"{what} timed out.");
        return await task;
    }

    public ValueTask DisposeAsync()
    {
        // Closing WebView2/Window can block briefly tearing down the browser process —
        // defer it to the next UI-thread tick instead of blocking the current async chain
        // (e.g. right after a script/postMessage round-trip) on that teardown. The profile
        // folder's own files can still be locked by that in-progress teardown, so give it a
        // moment before attempting the (best-effort) delete.
        var view = View;
        var window = _window;
        var userDataFolder = _userDataFolder;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { view.Close(); } catch { /* best effort */ }
            try { window.Close(); } catch { /* best effort */ }

            await Task.Delay(TimeSpan.FromSeconds(2));
            try { Directory.Delete(userDataFolder, recursive: true); } catch { /* best effort */ }
        });
        return ValueTask.CompletedTask;
    }
}
