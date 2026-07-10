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

        // theme: 'dark' matches the live viewer's own mermaid.initialize call (wwwroot/index.html)
        // so exported diagrams look the same as they do on screen.
        var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\" /><script>"
            + mermaidJs
            + "</script><script>mermaid.initialize({ startOnLoad: false, theme: 'dark' });</script></head><body></body></html>";

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
                            // Mermaid's own SVG root uses width="100%" plus an internal
                            // max-width style, which only resolves sensibly when it renders
                            // straight into the live page's DOM. Used standalone as an <img>
                            // source, that percentage has no defined reference size, so give
                            // it explicit intrinsic dimensions from its viewBox instead — the
                            // wrapping <img style="max-width:100%"> can then scale it down
                            // consistently from that known base size.
                            const doc = new DOMParser().parseFromString(svg, 'image/svg+xml');
                            const svgEl = doc.documentElement;
                            const viewBox = (svgEl.getAttribute('viewBox') || '').split(/\s+/).map(Number);
                            if (viewBox.length === 4 && viewBox.every(n => !isNaN(n))) {
                                svgEl.setAttribute('width', viewBox[2]);
                                svgEl.setAttribute('height', viewBox[3]);
                            }
                            results.push(new XMLSerializer().serializeToString(svgEl));
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
