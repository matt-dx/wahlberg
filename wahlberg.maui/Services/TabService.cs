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

    // A platform heuristic, not true filesystem detection: Windows/iOS/macCatalyst are
    // case-insensitive by default (though a non-default case-sensitive APFS volume is
    // possible on Apple platforms), Android's typically isn't. Every path-keyed dictionary
    // and comparison below uses this rather than hard-coding one or the other.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsIOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(400);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(PathComparer);
    private readonly Dictionary<string, CancellationTokenSource> _pendingReloads = new(PathComparer);

    // Bumped per file on every ScheduleReload call. ReloadDocumentFromDiskAsync stamps the
    // generation it was scheduled with and, once its (possibly slow) read+render finishes,
    // discards the result if a newer reload has since been scheduled for the same path — this
    // stops two overlapping reloads (from two quick saves) from applying out of order.
    private readonly Dictionary<string, int> _reloadGenerations = new(PathComparer);

    // Guards _watchers/_pendingReloads/_reloadGenerations, which FileSystemWatcher callbacks
    // (ThreadPool threads) touch alongside WatchFile/UnwatchFile (UI-driven).
    private readonly object _watcherStateLock = new();

    // Guards _openDocuments/ActiveDocument since FileSystemWatcher-driven reloads can otherwise
    // race with UI-driven Add/Close/SetActive calls. Never held at the same time as
    // _watcherStateLock — the two are always acquired one at a time, so lock ordering can't
    // deadlock.
    private readonly object _docsLock = new();

    private readonly List<MarkdownDocument> _openDocuments = [];
    public MarkdownDocument? ActiveDocument { get; private set; }
    public TabOrientation Orientation { get; set; } = TabOrientation.Horizontal;

    // Tracked separately so switching to a diff tab (which isn't persisted) doesn't
    // clobber the ActiveFile session restores to on next launch.
    private string? _lastActiveRealFile;

    // Used everywhere a FilePath is compared so _openDocuments dedup logic matches
    // the same platform-appropriate comparer as the _watchers/_pendingReloads keys.
    private static bool PathsEqual(string? a, string? b) => PathComparer.Equals(a, b);

    /// <summary>
    /// Looks up an open (non-diff) document by path using the same platform-appropriate
    /// comparer as AddDocumentAsync's own dedup check — callers should use this instead of
    /// exact string equality against FilePath, which can miss a match that differs only by
    /// case on case-insensitive platforms.
    /// </summary>
    public MarkdownDocument? FindOpenDocument(string filePath)
    {
        lock (_docsLock)
        {
            return _openDocuments.FirstOrDefault(d => !d.IsDiff && PathsEqual(d.FilePath, filePath));
        }
    }

    /// <summary>
    /// A point-in-time copy of the open document list. UI code should enumerate this instead
    /// of a live reference — TabService can be a singleton shared across multiple Blazor
    /// Server circuits (--serve mode), and a FileSystemWatcher-driven reload runs on a
    /// ThreadPool thread, so an unsynchronized enumeration of the live list could observe it
    /// mid-mutation and throw.
    /// </summary>
    public IReadOnlyList<MarkdownDocument> GetOpenDocumentsSnapshot()
    {
        lock (_docsLock)
        {
            return _openDocuments.ToList();
        }
    }

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
                    var active = _openDocuments.FirstOrDefault(d => !d.IsDiff && PathsEqual(d.FilePath, session.ActiveFile));
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
            existing = _openDocuments.FirstOrDefault(d => !d.IsDiff && PathsEqual(d.FilePath, filePath));
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

                _openDocuments.Add(doc);
                SetActive(doc);
            }
        }
        StateChanged?.Invoke();

        if (existing is not null)
        {
            // Reactivating an already-open tab still changes ActiveFile — persist it,
            // matching SetActiveDocument's behavior, so it survives a restart.
            if (saveSession)
                _ = SaveSessionAsync();
            return;
        }
        var newDoc = doc!;

        var (html, headings, frontMatter) = await Task.Run(() => RenderDocument(content, filePath));

        bool stillOpen;
        lock (_docsLock)
        {
            stillOpen = _openDocuments.Contains(newDoc);
            if (stillOpen)
            {
                newDoc.HtmlContent = html;
                newDoc.Headings = headings;
                newDoc.FrontMatter = frontMatter;
                newDoc.IsLoading = false;
            }
        }

        // The tab was closed while the render was in flight — don't touch the (no longer
        // open) document or start watching a file nothing references anymore.
        if (!stillOpen) return;

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
            var existing = _openDocuments.FirstOrDefault(d =>
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

                _openDocuments.Add(doc);
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
            var index = _openDocuments.IndexOf(doc);
            _openDocuments.Remove(doc);

            if (ActiveDocument == doc)
            {
                SetActive(_openDocuments.Count > 0
                    ? _openDocuments[Math.Min(index, _openDocuments.Count - 1)]
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

        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(dir, name)
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
                // Enabled while still holding the lock — UnwatchFile also takes this lock,
                // so it can't dispose this instance between registration and enabling
                // (which would otherwise throw ObjectDisposedException here).
                watcher.EnableRaisingEvents = true;
            }
        }
        catch
        {
            // Watching is best-effort; the file just won't auto-reload. But make sure a
            // partially-initialized watcher doesn't leak or linger in _watchers.
            if (watcher is not null)
            {
                lock (_watcherStateLock)
                {
                    if (_watchers.TryGetValue(filePath, out var registered) && ReferenceEquals(registered, watcher))
                        _watchers.Remove(filePath);
                }
                watcher.Dispose();
            }
            return;
        }

        // AddDocumentAsync releases _docsLock before calling WatchFile, so the tab could
        // have been closed while this watcher was being set up — if so, tear down what was
        // just registered rather than leaking a watcher nothing will ever unwatch again.
        bool stillOpen;
        lock (_docsLock)
        {
            stillOpen = _openDocuments.Any(d => !d.IsDiff && PathsEqual(d.FilePath, filePath));
        }
        if (!stillOpen)
            UnwatchFile(filePath);
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
            // A queued watcher event can still fire after UnwatchFile/Dispose already ran
            // (e.g. Dispose() unsubscribing races the last raised event) — ignore it rather
            // than re-populating state for a file that's no longer watched/open.
            if (!_watchers.ContainsKey(filePath)) return;

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
            doc = _openDocuments.FirstOrDefault(d => !d.IsDiff && PathsEqual(d.FilePath, filePath));
        }
        if (doc is null) return;

        string content;
        try
        {
            content = await ReadWithRetryAsync(filePath);
        }
        catch (IOException)
        {
            // Best-effort — e.g. a transient permissions hiccup. The next file-change
            // event will trigger another attempt.
            return;
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort — e.g. a transient permissions hiccup. The next file-change
            // event will trigger another attempt.
            return;
        }

        lock (_docsLock)
        {
            // FileSystemWatcher often fires more than once per save (and can fire even when
            // content didn't change) — skip the expensive Markdig render entirely if nothing
            // actually changed on disk.
            if (!_openDocuments.Contains(doc)) return;
            if (content == doc.Content) return;
        }

        var (html, headings, frontMatter) = await Task.Run(() => RenderDocument(content, filePath));

        lock (_watcherStateLock)
        {
            // A newer save superseded this one while we were reading/rendering — drop this
            // stale result so an out-of-order completion can't overwrite fresher content.
            if (_reloadGenerations.TryGetValue(filePath, out var latest) && latest != generation)
                return;
        }

        lock (_docsLock)
        {
            // Re-check the doc is still open and content still differs — both may have
            // changed while we were reloading.
            if (!_openDocuments.Contains(doc)) return;
            if (content == doc.Content) return;

            doc.Content = content;
            doc.HtmlContent = html;
            doc.Headings = headings;
            // FrontMatterExpanded is left untouched — an external edit shouldn't collapse or
            // expand a bar the user already toggled for this tab.
            doc.FrontMatter = frontMatter;
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
                openRealFiles = _openDocuments.Where(d => !d.IsDiff).Select(d => d.FilePath).ToList();

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

    /// <summary>
    /// Shared by <see cref="AddDocumentAsync"/> and <see cref="ReloadDocumentFromDiskAsync"/>:
    /// splits off any front matter, renders the remaining Markdown body, resolves local image
    /// paths, and extracts headings from the rendered HTML. Runs off the UI thread.
    /// </summary>
    private (string Html, List<HeadingInfo> Headings, FrontMatterInfo? FrontMatter) RenderDocument(string content, string filePath)
    {
        var (body, frontMatter) = FrontMatterParser.Extract(content);
        var rawHtml = Markdown.ToHtml(body, _pipeline);
        var docDir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(docDir))
            rawHtml = ResolveLocalPaths(rawHtml, docDir);
        return (rawHtml, ExtractHeadings(rawHtml), frontMatter);
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
