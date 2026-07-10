using Microsoft.Web.WebView2.Core;

namespace Wahlberg.Platforms.Windows;

// Renders composed export HTML to a PDF by driving a hidden WebView2 instance
// through CoreWebView2.PrintToPdfAsync.
internal static class PdfExporter
{
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
            printSettings.MarginTop = printSettings.MarginBottom = 0.4;
            printSettings.MarginLeft = printSettings.MarginRight = 0.4;
            printSettings.PageWidth = 8.5;
            printSettings.PageHeight = 11;

            var success = await hidden.View.CoreWebView2.PrintToPdfAsync(outputPath, printSettings);
            if (!success)
                throw new InvalidOperationException("WebView2 failed to produce a PDF.");
        }
        finally
        {
            try { File.Delete(tempHtmlPath); } catch { /* best effort */ }
        }
    }
}
