using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Wahlberg.Platforms.Windows;

// Renders composed export HTML to a PDF by driving a hidden WebView2 instance
// through CoreWebView2.PrintToPdfAsync.
internal static class PdfExporter
{
    private const double PageHeightIn = 11.0;
    private const double PageWidthIn = 8.5;
    private const double MarginIn = 0.4;

    public static Task ExportAsync(string html, string outputPath)
    {
        var tcs = new TaskCompletionSource<bool>();

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await RenderAsync(html, outputPath);
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    private static async Task RenderAsync(string html, string outputPath)
    {
        await using var hidden = await HiddenWebView.CreateAsync();

        var tempHtmlPath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid()}.html");
        await File.WriteAllTextAsync(tempHtmlPath, html);
        try
        {
            await hidden.NavigateAndWaitAsync(new Uri(tempHtmlPath).AbsoluteUri);

            var printSettings = hidden.Environment.CreatePrintSettings();
            printSettings.Orientation = CoreWebView2PrintOrientation.Portrait;
            printSettings.MarginTop = printSettings.MarginBottom = MarginIn;
            printSettings.MarginLeft = printSettings.MarginRight = MarginIn;
            printSettings.PageWidth = PageWidthIn;
            printSettings.PageHeight = PageHeightIn;

            await ApplyPageBreakAdjustmentsAsync(hidden.View.CoreWebView2);

            var success = await hidden.View.CoreWebView2.PrintToPdfAsync(outputPath, printSettings);
            if (!success)
                throw new InvalidOperationException("WebView2 failed to produce a PDF.");
        }
        finally
        {
            try { File.Delete(tempHtmlPath); } catch { /* best effort */ }
        }
    }

    // Pagination rules that depend on rendered size rather than static structure can't be
    // expressed as plain CSS (Chromium's fragmentation engine has no "unless taller than 50% of
    // a page" concept), so this measures the live, already-laid-out DOM in the hidden WebView2
    // and applies targeted inline break-inside:avoid overrides before printing:
    //   - a table is kept together unless its own height exceeds half a page's content height
    //     (a table taller than that is left splittable; DefaultExportCss's `tr { break-inside:
    //     avoid }` still keeps individual rows from being sliced mid-row)
    //   - a short paragraph (under ~5 rendered lines) is kept together outright, so it can never
    //     be left with just its last line stranded alone at the top of a new page
    // PrintToPdfAsync renders whatever is currently in the live DOM, not the originally-navigated
    // HTML string, so mutating in place here is picked up without re-navigating.
    private static async Task ApplyPageBreakAdjustmentsAsync(CoreWebView2 coreWebView2)
    {
        var pageContentHeightPx = (PageHeightIn - 2 * MarginIn) * 96;
        var script = $$"""
            (function() {
                const pageContentHeightPx = {{JsonSerializer.Serialize(pageContentHeightPx)}};

                document.querySelectorAll('table').forEach((table) => {
                    if (table.getBoundingClientRect().height <= pageContentHeightPx * 0.5) {
                        table.style.breakInside = 'avoid';
                        table.style.pageBreakInside = 'avoid';
                    }
                });

                document.querySelectorAll('p').forEach((p) => {
                    const lineHeight = parseFloat(getComputedStyle(p).lineHeight) || 20;
                    const lineCount = Math.round(p.getBoundingClientRect().height / lineHeight);
                    if (lineCount > 0 && lineCount < 5) {
                        p.style.breakInside = 'avoid';
                        p.style.pageBreakInside = 'avoid';
                    }
                });
            })();
            """;

        await coreWebView2.ExecuteScriptAsync(script);
    }
}
