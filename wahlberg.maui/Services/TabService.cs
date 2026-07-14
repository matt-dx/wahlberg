using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Wahlberg.Models;

namespace Wahlberg.Services;

public partial class TabService : IDisposable
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly string _sessionPath = Path.Combine(FileSystem.AppDataDirectory, "session.json");

    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(400);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pendingReloads = new(StringComparer.OrdinalIgnoreCase);

    public List<MarkdownDocument> OpenDocuments { get; } = [];
    public MarkdownDocument? ActiveDocument { get; private set; }
    public TabOrientation Orientation { get; set; } = TabOrientation.Horizontal;

    // Tracked separately so switching to a diff tab (which isn't persisted) doesn't
    // clobber the ActiveFile session restores to on next launch.
    private string? _lastActiveRealFile;

    private void SetActive(MarkdownDocument? doc)
    {
        ActiveDocument = doc;
        if (doc is { IsDiff: false })
            _lastActiveRealFile = doc.FilePath;
    }

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
                    SetActive(active);
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
            SetActive(existing);
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
        SetActive(doc);
        StateChanged?.Invoke();
        WatchFile(filePath);

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

    public void AddDiffDocument(
        MarkdownDocument left, MarkdownDocument right,
        string unifiedHtml, string sideBySideHtml,
        string renderedUnifiedHtml, string renderedSideBySideHtml,
        string unifiedText)
    {
        var title = $"{left.FileName} ↔ {right.FileName}";
        var existing = OpenDocuments.FirstOrDefault(d =>
            d.IsDiff && d.DiffLeftPath == left.FilePath && d.DiffRightPath == right.FilePath);
        if (existing is not null)
        {
            SetActive(existing);
            StateChanged?.Invoke();
            return;
        }

        var doc = new MarkdownDocument
        {
            FilePath = title,
            Content = unifiedText,
            IsDiff = true,
            DiffLeftLabel = left.FileName,
            DiffRightLabel = right.FileName,
            DiffLeftPath = left.FilePath,
            DiffRightPath = right.FilePath,
            DiffUnifiedHtml = unifiedHtml,
            DiffSideBySideHtml = sideBySideHtml,
            DiffRenderedUnifiedHtml = renderedUnifiedHtml,
            DiffRenderedSideBySideHtml = renderedSideBySideHtml,
            IsLoading = false
        };

        OpenDocuments.Add(doc);
        SetActive(doc);
        StateChanged?.Invoke();
    }

    public void SetActiveDocument(MarkdownDocument doc)
    {
        SetActive(doc);
        StateChanged?.Invoke();
        _ = SaveSessionAsync();
    }

    public void CloseDocument(MarkdownDocument doc)
    {
        var index = OpenDocuments.IndexOf(doc);
        OpenDocuments.Remove(doc);

        if (!doc.IsDiff)
            UnwatchFile(doc.FilePath);

        if (ActiveDocument == doc)
        {
            SetActive(OpenDocuments.Count > 0
                ? OpenDocuments[Math.Min(index, OpenDocuments.Count - 1)]
                : null);
        }

        StateChanged?.Invoke();
        _ = SaveSessionAsync();
    }

    public void NotifyStateChanged() => StateChanged?.Invoke();

    /// <summary>
    /// Watches a real (non-diff) document's backing file so external edits are picked up
    /// and reloaded automatically. Best-effort — failures (e.g. unsupported path) are ignored.
    /// </summary>
    private void WatchFile(string filePath)
    {
        if (_watchers.ContainsKey(filePath)) return;

        var dir = Path.GetDirectoryName(filePath);
        var name = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || !Directory.Exists(dir)) return;

        try
        {
            var watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            watcher.Changed += (_, _) => ScheduleReload(filePath);
            watcher.Created += (_, _) => ScheduleReload(filePath);
            watcher.Renamed += (_, _) => ScheduleReload(filePath);
            watcher.EnableRaisingEvents = true;
            _watchers[filePath] = watcher;
        }
        catch
        {
            // Watching is best-effort; the file just won't auto-reload.
        }
    }

    private void UnwatchFile(string filePath)
    {
        if (_watchers.Remove(filePath, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        lock (_pendingReloads)
        {
            if (_pendingReloads.Remove(filePath, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }

    /// <summary>Debounces bursts of filesystem events (editors often fire several per save) before reloading.</summary>
    private void ScheduleReload(string filePath)
    {
        CancellationTokenSource cts;
        lock (_pendingReloads)
        {
            if (_pendingReloads.TryGetValue(filePath, out var existing))
            {
                existing.Cancel();
                existing.Dispose();
            }

            cts = new CancellationTokenSource();
            _pendingReloads[filePath] = cts;
        }

        _ = DebouncedReloadAsync(filePath, cts.Token);
    }

    private async Task DebouncedReloadAsync(string filePath, CancellationToken token)
    {
        try
        {
            await Task.Delay(ReloadDebounce, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        lock (_pendingReloads)
        {
            _pendingReloads.Remove(filePath);
        }

        await ReloadDocumentFromDiskAsync(filePath);
    }

    private async Task ReloadDocumentFromDiskAsync(string filePath)
    {
        var doc = OpenDocuments.FirstOrDefault(d => !d.IsDiff && d.FilePath == filePath);
        if (doc is null) return;

        string content;
        try
        {
            content = await ReadWithRetryAsync(filePath);
        }
        catch (IOException)
        {
            return;
        }

        if (content == doc.Content) return;

        var (html, headings) = await Task.Run(() =>
        {
            var rawHtml = Markdown.ToHtml(content, _pipeline);
            var docDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(docDir))
                rawHtml = ResolveLocalPaths(rawHtml, docDir);
            return (rawHtml, ExtractHeadings(rawHtml));
        });

        doc.Content = content;
        doc.HtmlContent = html;
        doc.Headings = headings;
        doc.ReloadVersion++;
        StateChanged?.Invoke();
    }

    /// <summary>A save can briefly hold the file locked (e.g. temp-file-then-rename); retry a few times before giving up.</summary>
    private static async Task<string> ReadWithRetryAsync(string filePath)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(filePath);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(150);
            }
            catch (FileNotFoundException) when (attempt < maxAttempts)
            {
                await Task.Delay(150);
            }
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        lock (_pendingReloads)
        {
            foreach (var cts in _pendingReloads.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _pendingReloads.Clear();
        }
    }

    private async Task SaveSessionAsync()
    {
        try
        {
            var openRealFiles = OpenDocuments.Where(d => !d.IsDiff).Select(d => d.FilePath).ToList();

            // Only trust the diff-tab fallback if that file is still actually open — it can go
            // stale if the real document behind it was closed while a diff tab stayed active.
            var activeFile = ActiveDocument is { IsDiff: false }
                ? ActiveDocument.FilePath
                : _lastActiveRealFile is not null && openRealFiles.Contains(_lastActiveRealFile)
                    ? _lastActiveRealFile
                    : null;

            var session = new SessionState
            {
                OpenFiles = openRealFiles,
                ActiveFile = activeFile
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
                Text = WebUtility.HtmlDecode(HtmlTagRegex().Replace(match.Groups[3].Value, "")).Trim()
            });
        }

        return headings;
    }

    [GeneratedRegex(@"<h([1-6])\s+id=""([^""]+)""[^>]*>(.*?)</h\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"(<img\b[^>]*?\bsrc\s*=\s*"")([^""]+)("")", RegexOptions.IgnoreCase)]
    internal static partial Regex ImgSrcRegex();

    /// <summary>
    /// Rewrites relative image src paths to virtual-host URLs that WebView2 can resolve to local files.
    /// </summary>
    internal static string ResolveLocalPaths(string html, string documentDirectory)
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
