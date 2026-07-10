using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Wahlberg.Platforms.Windows;

// Renders Mermaid diagram source to SVG markup by driving mermaid.js (the same vendored
// copy used by the live viewer, wwwroot/js/mermaid.min.js) inside a hidden WebView2, since
// there is no way to render a Mermaid diagram outside of a real browser engine.
internal static class MermaidRenderer
{
    public static Task<List<string>> RenderAllAsync(List<string> sources)
    {
        var tcs = new TaskCompletionSource<List<string>>();

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                tcs.SetResult(await RenderAllCoreAsync(sources));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    private static async Task<List<string>> RenderAllCoreAsync(List<string> sources)
    {
        if (sources.Count == 0) return [];

        var mermaidJsPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "js", "mermaid.min.js");
        if (!File.Exists(mermaidJsPath)) return sources.Select(_ => "").ToList();
        var mermaidJs = await File.ReadAllTextAsync(mermaidJsPath);

        await using var hidden = await HiddenWebView.CreateAsync();

        var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\" /><script>"
            + mermaidJs
            + "</script><script>mermaid.initialize({ startOnLoad: false });</script></head><body></body></html>";

        var tempHtmlPath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid()}.html");
        await File.WriteAllTextAsync(tempHtmlPath, html);
        try
        {
            await hidden.NavigateAndWaitAsync(new Uri(tempHtmlPath).AbsoluteUri);

            var sourcesJson = JsonSerializer.Serialize(sources);
            var script = $$"""
                (async () => {
                    const sources = {{sourcesJson}};
                    const results = [];
                    for (let i = 0; i < sources.length; i++) {
                        try {
                            const { svg } = await mermaid.render('mmd-' + i, sources[i]);
                            results.push(svg);
                        } catch (e) {
                            results.push('');
                        }
                    }
                    window.chrome.webview.postMessage(results);
                })();
                """;

            // ExecuteScriptAsync's own return value does not reliably await an async
            // script's promise, so completion is signaled via postMessage instead.
            var messageTcs = new TaskCompletionSource<string>();
            void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args) =>
                messageTcs.TrySetResult(args.WebMessageAsJson);

            hidden.View.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            try
            {
                await hidden.View.CoreWebView2.ExecuteScriptAsync(script);
                var completed = await Task.WhenAny(messageTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                if (completed != messageTcs.Task)
                    return sources.Select(_ => "").ToList();

                var json = await messageTcs.Task;
                return JsonSerializer.Deserialize<List<string>>(json) ?? sources.Select(_ => "").ToList();
            }
            finally
            {
                hidden.View.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }
        }
        finally
        {
            try { File.Delete(tempHtmlPath); } catch { /* best effort */ }
        }
    }
}
