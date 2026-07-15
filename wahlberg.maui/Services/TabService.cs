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

    // Bumped per file on every ScheduleReload call. ReloadDocumentFromDiskAsync stamps the
    // generation it was scheduled with and, once its (possibly slow) read+render finishes,
    // discards the result if a newer reload has since been scheduled for the same path — this
    // stops two overlapping reloads (from two quick saves) from applying out of order.
    private readonly Dictionary<string, int> _reloadGenerations = new(StringComparer.OrdinalIgnoreCase);

    // Guards _watchers/_pendingReloads/_reloadGenerations, which FileSystemWatcher callbacks
    // (ThreadPool threads) touch alongside WatchFile/UnwatchFile (UI-driven).
    private readonly object _watcherStateLock = new();

    // Guards OpenDocuments/ActiveDocument since FileSystemWatcher-driven reloads can otherwise
    // race with UI-driven Add/Close/SetActive calls. Never held at the same time as
    // _watcherStateLock — the two are always acquired one at a time, so lock ordering can't
    // deadlock.
    private readonly object _docsLock = new();

    public List<MarkdownDocument> OpenDocuments { get; } = [];
    public MarkdownDocument? ActiveDocument { get; private set; }
    public TabOrientation Orientation { get; set; } = TabOrientation.Horizontal;

    // Tracked separately so switching to a diff tab (which isn't persisted) doesn't
    // clobber the ActiveFile session restores to on next launch.
    private string? _lastActiveRealFile;

    // Windows paths are case-insensitive; used everywhere a FilePath is compared so
    // OpenDocuments dedup logic matches the case-insensitive _watchers/_pendingReloads keys.
    private static bool PathsEqual(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
                bool activated;
                lock (_docsLock)
                {
                    var active = OpenDocuments.FirstOrDefault(d => PathsEqual(d.FilePath, session.ActiveFile));
                    activated = active is not null;
                    if (active is not null)
                        SetActive(active);
                }
                if (activated)
                    StateChanged?.Invoke();
            }
        }
        catch
        {
            // Ignore corrupt session file
        }
    }

    public async Task AddDocumentAsync(string filePath, string content, bool saveSession = true)
    {
        MarkdownDocument? existing;
        MarkdownDocument? doc = null;
        lock (_docsLock)
        {
            existing = OpenDocuments.FirstOrDefault(d => PathsEqual(d.FilePath, filePath));
            if (existing is not null)
            {
                SetActive(existing);
            }
            else
            {
                doc = new MarkdownDocument
                {
                    FilePath = filePath,
                    Content = content,
                    IsLoading = true
                };

                OpenDocuments.Add(doc);
                SetActive(doc);
            }
        }
        StateChanged?.Invoke();

        if (existing is not null) return;
        var newDoc = doc!;

        var (html, headings) = await Task.Run(() =>
        {
            var rawHtml = Markdown.ToHtml(content, _pipeline);
            var docDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(docDir))
                rawHtml = ResolveLocalPaths(rawHtml, docDir);
            return (rawHtml, ExtractHeadings(rawHtml));
        });

        newDoc.HtmlContent = html;
        newDoc.Headings = headings;
        newDoc.IsLoading = false;
        StateChanged?.Invoke();

        // Only start watching once the initial load has landed — otherwise a change event
        // firing mid-load could reload stale content that the load above then overwrites.
        WatchFile(filePath);

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
        lock (_docsLock)
        {
            var existing = OpenDocuments.FirstOrDefault(d =>
                d.IsDiff && PathsEqual(d.DiffLeftPath, left.FilePath) && PathsEqual(d.DiffRightPath, right.FilePath));
            if (existing is not null)
            {
                SetActive(existing);
            }
            else
            {
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
            }
        }
        StateChanged?.Invoke();
    }

    public void SetActiveDocument(MarkdownDocument doc)
    {
        lock (_docsLock)
        {
            SetActive(doc);
        }
        StateChanged?.Invoke();
        _ = SaveSessionAsync();
    }

    public void CloseDocument(MarkdownDocument doc)
    {
        lock (_docsLock)
        {
            var index = OpenDocuments.IndexOf(doc);
            OpenDocuments.Remove(doc);

            if (ActiveDocument == doc)
            {
                SetActive(OpenDocuments.Count > 0
                    ? OpenDocuments[Math.Min(index, OpenDocuments.Count - 1)]
                    : null);
            }
        }

        if (!doc.IsDiff)
            UnwatchFile(doc.FilePath);

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
        lock (_watcherStateLock)
        {
            if (_watchers.ContainsKey(filePath)) return;
        }

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

            lock (_watcherStateLock)
            {
                if (_watchers.ContainsKey(filePath))
                {
                    watcher.Dispose();
                    return;
                }
                _watchers[filePath] = watcher;
            }
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Watching is best-effort; the file just won't auto-reload.
        }
    }

    private void UnwatchFile(string filePath)
    {
        FileSystemWatcher? watcher;
        CancellationTokenSource? cts;
        lock (_watcherStateLock)
        {
            _watchers.Remove(filePath, out watcher);
            _pendingReloads.Remove(filePath, out cts);
            _reloadGenerations.Remove(filePath);
        }

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        // Cancel only — the owning DebouncedReloadAsync's finally block disposes its own CTS.
        // Disposing it here too would race a concurrent Dispose() call on the same instance.
        cts?.Cancel();
    }

    /// <summary>Debounces bursts of filesystem events (editors often fire several per save) before reloading.</summary>
    private void ScheduleReload(string filePath)
    {
        CancellationTokenSource cts;
        int generation;
        lock (_watcherStateLock)
        {
            // Cancel only — see the comment in UnwatchFile about not disposing here.
            if (_pendingReloads.TryGetValue(filePath, out var existing))
                existing.Cancel();

            cts = new CancellationTokenSource();
            _pendingReloads[filePath] = cts;

            generation = _reloadGenerations.TryGetValue(filePath, out var g) ? g + 1 : 1;
            _reloadGenerations[filePath] = generation;
        }

        _ = DebouncedReloadAsync(filePath, cts, generation);
    }

    private async Task DebouncedReloadAsync(string filePath, CancellationTokenSource cts, int generation)
    {
        try
        {
            await Task.Delay(ReloadDebounce, cts.Token);
            await ReloadDocumentFromDiskAsync(filePath, generation);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer change event; nothing to do.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reload error for '{filePath}': {ex.Message}");
        }
        finally
        {
            lock (_watcherStateLock)
            {
                // Only remove/dispose if we're still the current pending entry — a newer
                // ScheduleReload call may have already replaced (and will dispose) us.
                if (_pendingReloads.TryGetValue(filePath, out var current) && current == cts)
                    _pendingReloads.Remove(filePath);
            }
            cts.Dispose();
        }
    }

    private async Task ReloadDocumentFromDiskAsync(string filePath, int generation)
    {
        MarkdownDocument? doc;
        lock (_docsLock)
        {
            doc = OpenDocuments.FirstOrDefault(d => !d.IsDiff && PathsEqual(d.FilePath, filePath));
        }
        if (doc is null) return;

        string content;
        try
        {
            content = await ReadWithRetryAsync(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — e.g. a transient permissions hiccup. The next file-change
            // event will trigger another attempt.
            return;
        }

        var (html, headings) = await Task.Run(() =>
        {
            var rawHtml = Markdown.ToHtml(content, _pipeline);
            var docDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(docDir))
                rawHtml = ResolveLocalPaths(rawHtml, docDir);
            return (rawHtml, ExtractHeadings(rawHtml));
        });

        lock (_watcherStateLock)
        {
            // A newer save superseded this one while we were reading/rendering — drop this
            // stale result so an out-of-order completion can't overwrite fresher content.
            if (_reloadGenerations.TryGetValue(filePath, out var latest) && latest != generation)
                return;
        }

        lock (_docsLock)
        {
            // Re-check the doc is still open — it may have been closed while we were reloading.
            if (!OpenDocuments.Contains(doc)) return;
            if (content == doc.Content) return;

            doc.Content = content;
            doc.HtmlContent = html;
            doc.Headings = headings;
            doc.ReloadVersion++;
        }
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
                // FileNotFoundException derives from IOException (a save can transiently
                // delete-then-recreate the file), so this one catch covers both.
                await Task.Delay(150);
            }
        }
    }

    public void Dispose()
    {
        lock (_watcherStateLock)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();

            // Cancel only — see the comment in UnwatchFile about not disposing here.
            foreach (var cts in _pendingReloads.Values)
                cts.Cancel();
            _pendingReloads.Clear();
            _reloadGenerations.Clear();
        }
    }

    private async Task SaveSessionAsync()
    {
        try
        {
            List<string> openRealFiles;
            string? activeFile;
            lock (_docsLock)
            {
                openRealFiles = OpenDocuments.Where(d => !d.IsDiff).Select(d => d.FilePath).ToList();

                // Only trust the diff-tab fallback if that file is still actually open — it can go
                // stale if the real document behind it was closed while a diff tab stayed active.
                activeFile = ActiveDocument is { IsDiff: false }
                    ? ActiveDocument.FilePath
                    : _lastActiveRealFile is not null && openRealFiles.Any(f => PathsEqual(f, _lastActiveRealFile))
                        ? _lastActiveRealFile
                        : null;
            }

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
