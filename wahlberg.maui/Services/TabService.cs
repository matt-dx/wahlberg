using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Wahlberg.Models;

namespace Wahlberg.Services;

public partial class TabService
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly string _sessionPath = Path.Combine(FileSystem.AppDataDirectory, "session.json");

    public List<MarkdownDocument> OpenDocuments { get; } = [];
    public MarkdownDocument? ActiveDocument { get; private set; }
    public TabOrientation Orientation { get; set; } = TabOrientation.Horizontal;

    public event Action? StateChanged;

    public async Task RestoreSessionAsync()
    {
        if (!File.Exists(_sessionPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(_sessionPath);
            var session = JsonSerializer.Deserialize<SessionState>(json);
            if (session?.OpenFiles is null || session.OpenFiles.Count == 0) return;

            foreach (var filePath in session.OpenFiles)
            {
                if (File.Exists(filePath))
                {
                    var content = await File.ReadAllTextAsync(filePath);
                    await AddDocumentAsync(filePath, content, saveSession: false);
                }
            }

            // Restore active tab
            if (!string.IsNullOrEmpty(session.ActiveFile))
            {
                var active = OpenDocuments.FirstOrDefault(d => d.FilePath == session.ActiveFile);
                if (active is not null)
                {
                    ActiveDocument = active;
                    StateChanged?.Invoke();
                }
            }
        }
        catch
        {
            // Ignore corrupt session file
        }
    }

    public async Task AddDocumentAsync(string filePath, string content, bool saveSession = true)
    {
        var existing = OpenDocuments.FirstOrDefault(d => d.FilePath == filePath);
        if (existing is not null)
        {
            ActiveDocument = existing;
            StateChanged?.Invoke();
            return;
        }

        var doc = new MarkdownDocument
        {
            FilePath = filePath,
            Content = content,
            IsLoading = true
        };

        OpenDocuments.Add(doc);
        ActiveDocument = doc;
        StateChanged?.Invoke();

        var (html, headings) = await Task.Run(() =>
        {
            var rawHtml = Markdown.ToHtml(content, _pipeline);
            var docDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(docDir))
                rawHtml = ResolveLocalPaths(rawHtml, docDir);
            return (rawHtml, ExtractHeadings(rawHtml));
        });

        doc.HtmlContent = html;
        doc.Headings = headings;
        doc.IsLoading = false;
        StateChanged?.Invoke();

        if (saveSession)
            _ = SaveSessionAsync();
    }

    public void SetActiveDocument(MarkdownDocument doc)
    {
        ActiveDocument = doc;
        StateChanged?.Invoke();
        _ = SaveSessionAsync();
    }

    public void CloseDocument(MarkdownDocument doc)
    {
        var index = OpenDocuments.IndexOf(doc);
        OpenDocuments.Remove(doc);

        if (ActiveDocument == doc)
        {
            ActiveDocument = OpenDocuments.Count > 0
                ? OpenDocuments[Math.Min(index, OpenDocuments.Count - 1)]
                : null;
        }

        StateChanged?.Invoke();
        _ = SaveSessionAsync();
    }

    public void NotifyStateChanged() => StateChanged?.Invoke();

    private async Task SaveSessionAsync()
    {
        try
        {
            var session = new SessionState
            {
                OpenFiles = OpenDocuments.Select(d => d.FilePath).ToList(),
                ActiveFile = ActiveDocument?.FilePath
            };
            var json = JsonSerializer.Serialize(session);
            await File.WriteAllTextAsync(_sessionPath, json);
        }
        catch
        {
            // Non-critical — silently ignore
        }
    }

    private sealed class SessionState
    {
        public List<string> OpenFiles { get; set; } = [];
        public string? ActiveFile { get; set; }
    }

    private static List<HeadingInfo> ExtractHeadings(string html)
    {
        var headings = new List<HeadingInfo>();
        var matches = HeadingRegex().Matches(html);

        foreach (Match match in matches)
        {
            headings.Add(new HeadingInfo
            {
                Level = int.Parse(match.Groups[1].Value),
                Id = match.Groups[2].Value,
                Text = HtmlTagRegex().Replace(match.Groups[3].Value, "").Trim()
            });
        }

        return headings;
    }

    [GeneratedRegex(@"<h([1-6])\s+id=""([^""]+)""[^>]*>(.*?)</h\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"(<img\b[^>]*?\bsrc\s*=\s*"")([^""]+)("")", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcRegex();

    /// <summary>
    /// Rewrites relative image src paths to virtual-host URLs that WebView2 can resolve to local files.
    /// </summary>
    private static string ResolveLocalPaths(string html, string documentDirectory)
    {
        return ImgSrcRegex().Replace(html, match =>
        {
            var prefix = match.Groups[1].Value;
            var src = match.Groups[2].Value;
            var suffix = match.Groups[3].Value;

            // Skip absolute URLs and data URIs
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            try
            {
                var absolutePath = Path.GetFullPath(Path.Combine(documentDirectory, src));
                if (!File.Exists(absolutePath))
                    return match.Value;

                var url = ToLocalFileUrl(absolutePath);
                return $"{prefix}{url}{suffix}";
            }
            catch
            {
                return match.Value;
            }
        });
    }

    /// <summary>
    /// Converts an absolute file path to a virtual-host URL: https://localfile-{drive}/path/to/file
    /// </summary>
    internal static string ToLocalFileUrl(string absolutePath)
    {
        var fullPath = Path.GetFullPath(absolutePath);
        var drive = char.ToLowerInvariant(fullPath[0]);
        // Strip "C:\" prefix, convert backslashes to forward slashes
        var relativePath = fullPath[3..].Replace('\\', '/');
        // URL-encode path segments but preserve /
        var encoded = string.Join('/',
            relativePath.Split('/').Select(Uri.EscapeDataString));
        return $"https://localfile-{drive}/{encoded}";
    }
}
