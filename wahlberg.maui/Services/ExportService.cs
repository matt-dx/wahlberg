using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Wahlberg.Models;

namespace Wahlberg.Services;

public partial class ExportService
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public async Task ExportAsync(Wahlberg.Models.MarkdownDocument doc, ExportOptions options, string outputPath)
    {
        if (options.Format == ExportFormat.EmbeddedMarkdown)
        {
            await ExportEmbeddedMarkdownAsync(doc, outputPath);
            return;
        }

        var docDir = Path.GetDirectoryName(doc.FilePath) ?? "";

        var (sections, allHeadings) = await Task.Run(() =>
        {
            var secs = SplitContent(doc.Content, _pipeline, options.SplitOnHorizontalRule, options.SplitAtHeadingLevel);
            for (var i = 0; i < secs.Count; i++)
                secs[i] = new ExportSection { Heading = secs[i].Heading, Html = InlineImagesAsDataUris(secs[i].Html, docDir) };

            var headings = options.IncludeToc ? ExtractAllHeadings(doc.Content, _pipeline) : [];
            return (secs, headings);
        });

        if (options.Format == ExportFormat.Pdf)
            await ExportPdfAsync(options, sections, allHeadings, outputPath);
        else
            await ExportEpubAsync(doc, options, sections, allHeadings, outputPath);
    }

    // Splits the document's Markdig AST into sections at thematic-break and/or heading-level
    // boundaries. Splitting on the AST (rather than the rendered HTML string) gives precise
    // structural boundaries regardless of what markup happens to appear inside code blocks/tables.
    public List<ExportSection> SplitContent(string markdown, MarkdownPipeline pipeline, bool splitOnHr, int? splitHeadingLevel)
    {
        var document = Markdig.Parsers.MarkdownParser.Parse(markdown, pipeline);
        var sections = new List<ExportSection>();
        var current = new List<Block>();
        HeadingInfo? currentHeading = null;

        void Flush()
        {
            if (current.Count == 0) return;
            using var sw = new StringWriter();
            var renderer = new HtmlRenderer(sw);
            pipeline.Setup(renderer);
            foreach (var block in current) renderer.Render(block);
            sections.Add(new ExportSection { Heading = currentHeading, Html = sw.ToString() });
            current = [];
            currentHeading = null;
        }

        foreach (var block in document)
        {
            if (splitOnHr && block is ThematicBreakBlock)
            {
                Flush(); // the "---" itself is a boundary, not emitted as content
                continue;
            }

            if (splitHeadingLevel is int maxLevel && block is HeadingBlock heading && heading.Level <= maxLevel)
            {
                Flush();
                currentHeading = new HeadingInfo
                {
                    Level = heading.Level,
                    Id = heading.GetAttributes()?.Id ?? "",
                    Text = InlineToPlainText(heading.Inline)
                };
            }

            current.Add(block);
        }
        Flush();

        if (sections.Count == 0)
            sections.Add(new ExportSection { Heading = null, Html = "" });

        return sections;
    }

    // The auto-identifier extension (part of UseAdvancedExtensions) assigns heading ids during
    // Parse(), so ids are available on the AST without needing to render first.
    public List<HeadingInfo> ExtractAllHeadings(string markdown, MarkdownPipeline pipeline)
    {
        var document = Markdig.Parsers.MarkdownParser.Parse(markdown, pipeline);
        return document.Descendants<HeadingBlock>()
            .Select(h => new HeadingInfo
            {
                Level = h.Level,
                Id = h.GetAttributes()?.Id ?? "",
                Text = InlineToPlainText(h.Inline)
            })
            .ToList();
    }

    // Produces a single self-contained .md file: the original Markdown source, unchanged
    // except that image links and Mermaid diagrams are rewritten to embedded base64 data
    // URIs. Operates on raw Markdown text (not rendered HTML) so the output stays valid,
    // editable Markdown. Mermaid diagrams can only be rendered to an image via a real
    // browser engine, so that part is Windows-only; images are embedded on every platform.
    private async Task ExportEmbeddedMarkdownAsync(Wahlberg.Models.MarkdownDocument doc, string outputPath)
    {
        var docDir = Path.GetDirectoryName(doc.FilePath) ?? "";
        var markdown = doc.Content;
        var parsed = Markdig.Parsers.MarkdownParser.Parse(markdown, _pipeline);

        var replacements = new List<(int Start, int End, string Text)>();

        var mermaidBlocks = parsed.Descendants<FencedCodeBlock>()
            .Where(b => string.Equals(b.Info, "mermaid", StringComparison.OrdinalIgnoreCase))
            .ToList();

#if WINDOWS
        if (mermaidBlocks.Count > 0)
        {
            var sources = mermaidBlocks.Select(b => b.Lines.ToString()).ToList();
            var svgs = await Platforms.Windows.MermaidRenderer.RenderAllAsync(sources);
            for (var i = 0; i < mermaidBlocks.Count; i++)
            {
                var svg = i < svgs.Count ? svgs[i] : "";
                if (string.IsNullOrEmpty(svg)) continue;
                var dataUri = $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";
                var span = mermaidBlocks[i].Span;
                // Wrapped in a bordered/padded card (matching the live viewer's .mermaid-rendered
                // style) so diagrams of wildly different native sizes still look consistent —
                // a bare ![]() image has nothing to unify their scale. Raw HTML + inline styles
                // keep this portable to viewers without the app's CSS (GitHub, VS Code, etc.).
                var framed = "<div style=\"text-align:center;margin:1.5em 0;padding:16px;background:#252526;border:1px solid #888;border-radius:6px;\">\n"
                    + $"<img src=\"{dataUri}\" alt=\"Mermaid diagram\" style=\"max-width:100%;height:auto;\" />\n"
                    + "</div>";
                replacements.Add((span.Start, span.End, framed));
            }
        }
#endif

        foreach (var link in parsed.Descendants<LinkInline>().Where(l => l.IsImage))
        {
            var url = link.Url;
            if (string.IsNullOrEmpty(url) ||
                url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryResolveWithinDirectory(docDir, url, out var absolutePath)) continue;
            if (!File.Exists(absolutePath)) continue;

            var bytes = await File.ReadAllBytesAsync(absolutePath);
            var mime = MimeTypeFor(Path.GetExtension(absolutePath));
            var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

            var span = link.Span;
            var original = markdown.Substring(span.Start, span.End - span.Start + 1);
            var urlIndex = original.IndexOf(url, StringComparison.Ordinal);
            var replaced = urlIndex >= 0
                ? original[..urlIndex] + dataUri + original[(urlIndex + url.Length)..]
                : original;
            replacements.Add((span.Start, span.End, replaced));
        }

        replacements.Sort((a, b) => a.Start.CompareTo(b.Start));

        var sb = new StringBuilder();
        var cursor = 0;
        foreach (var (start, end, text) in replacements)
        {
            if (start < cursor) continue; // overlapping — keep original text, skip
            sb.Append(markdown, cursor, start - cursor);
            sb.Append(text);
            cursor = end + 1;
        }
        sb.Append(markdown, cursor, markdown.Length - cursor);

        await File.WriteAllTextAsync(outputPath, sb.ToString());
    }

    private static string InlineToPlainText(Inline? inline)
    {
        var sb = new StringBuilder();

        void Walk(Inline? node)
        {
            while (node is not null)
            {
                switch (node)
                {
                    case LiteralInline literal:
                        sb.Append(literal.Content.ToString());
                        break;
                    case CodeInline code:
                        sb.Append(code.Content);
                        break;
                    case LineBreakInline:
                        sb.Append(' ');
                        break;
                    case ContainerInline container:
                        Walk(container.FirstChild);
                        break;
                }
                node = node.NextSibling;
            }
        }

        Walk(inline);
        return sb.ToString().Trim();
    }

    public static string BuildTocHtml(List<HeadingInfo> headings, HashSet<int> levels, Func<HeadingInfo, string> hrefFor)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Table of Contents</h1><ol class=\"export-toc\">");
        foreach (var h in headings.Where(h => levels.Contains(h.Level)))
            sb.Append($"<li class=\"toc-level-{h.Level}\"><a href=\"{hrefFor(h)}\">{System.Net.WebUtility.HtmlEncode(h.Text)}</a></li>");
        sb.Append("</ol>");
        return sb.ToString();
    }

    private static int FindSectionIndexForHeadingId(List<ExportSection> sections, string headingId)
    {
        if (string.IsNullOrEmpty(headingId)) return 0;
        for (var i = 0; i < sections.Count; i++)
            if (sections[i].Html.Contains($"id=\"{headingId}\"", StringComparison.Ordinal))
                return i;
        return 0;
    }

    internal static string InlineImagesAsDataUris(string html, string documentDirectory)
    {
        return TabService.ImgSrcRegex().Replace(html, match =>
        {
            var prefix = match.Groups[1].Value;
            var src = match.Groups[2].Value;
            var suffix = match.Groups[3].Value;

            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            try
            {
                if (!TryResolveWithinDirectory(documentDirectory, src, out var absolutePath)) return match.Value;
                if (!File.Exists(absolutePath)) return match.Value;

                var bytes = File.ReadAllBytes(absolutePath);
                var mime = MimeTypeFor(Path.GetExtension(absolutePath));
                return $"{prefix}data:{mime};base64,{Convert.ToBase64String(bytes)}{suffix}";
            }
            catch
            {
                return match.Value;
            }
        });
    }

    private static string MimeTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream"
    };

    // Resolves relativePath against baseDirectory and rejects anything that escapes it (e.g. via
    // "../"), so a document can't embed and re-share arbitrary files elsewhere on disk.
    private static bool TryResolveWithinDirectory(string baseDirectory, string relativePath, out string absolutePath)
    {
        absolutePath = "";
        try
        {
            var baseFull = Path.GetFullPath(baseDirectory);
            var candidate = Path.GetFullPath(Path.Combine(baseFull, relativePath));
            var baseWithSeparator = baseFull.EndsWith(Path.DirectorySeparatorChar) ? baseFull : baseFull + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(baseWithSeparator, StringComparison.OrdinalIgnoreCase))
                return false;

            absolutePath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"<(hr|br|img)\b([^>]*?)(?<!/)>", RegexOptions.IgnoreCase)]
    private static partial Regex VoidElementRegex();

    private static string SelfCloseVoidElements(string html) =>
        VoidElementRegex().Replace(html, m => $"<{m.Groups[1].Value}{m.Groups[2].Value} />");

    private const string DefaultExportCss = """
        body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; line-height: 1.6; color: #1a1a1a; margin: 0; }
        .export-page { padding: 1in 0.9in; }
        .page-break { page-break-before: always; }
        .export-page.cover-page { padding: 0; height: 10.2in; box-sizing: border-box; overflow: hidden; }
        .export-cover { display: flex; align-items: center; justify-content: center; height: 100%; }
        .export-cover img { max-width: 100%; max-height: 100%; }
        img { max-width: 100%; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border: 1px solid #ccc; padding: 6px 10px; }
        pre { background: #f4f4f4; padding: 10px; overflow-x: auto; }
        code { font-family: "Cascadia Code", "Fira Code", Consolas, monospace; }
        .export-toc ol, .export-toc { list-style: none; padding-left: 0; }
        .export-toc li { margin: 4px 0; }
        .export-toc .toc-level-2 { padding-left: 1em; }
        .export-toc .toc-level-3 { padding-left: 2em; }
        .export-toc .toc-level-4 { padding-left: 3em; }
        .export-toc .toc-level-5 { padding-left: 4em; }
        .export-toc .toc-level-6 { padding-left: 5em; }
        """;

    private async Task ExportPdfAsync(ExportOptions options, List<ExportSection> sections, List<HeadingInfo> allHeadings, string outputPath)
    {
        var items = new List<(string Html, string ExtraClass)>();

        if (options.CoverPage == CoverPageMode.ExternalImage &&
            !string.IsNullOrEmpty(options.CoverImagePath) && File.Exists(options.CoverImagePath))
        {
            var bytes = await File.ReadAllBytesAsync(options.CoverImagePath);
            var mime = MimeTypeFor(Path.GetExtension(options.CoverImagePath));
            items.Add(($"<section class=\"export-cover\"><img src=\"data:{mime};base64,{Convert.ToBase64String(bytes)}\" alt=\"Cover\" /></section>", "cover-page"));
        }

        var startIndex = 0;
        if (options.CoverPage == CoverPageMode.FirstSection && sections.Count > 0)
        {
            items.Add(($"<section>{sections[0].Html}</section>", ""));
            startIndex = 1;
        }

        if (options.IncludeToc)
        {
            var tocHtml = BuildTocHtml(allHeadings, options.TocHeadingLevels, h => $"#{h.Id}");
            items.Add(($"<section class=\"export-toc-page\">{tocHtml}</section>", ""));
        }

        for (var i = startIndex; i < sections.Count; i++)
            items.Add(($"<section>{sections[i].Html}</section>", ""));

        var body = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var classes = "export-page" + (i == 0 ? "" : " page-break") +
                (string.IsNullOrEmpty(items[i].ExtraClass) ? "" : $" {items[i].ExtraClass}");
            body.Append($"<div class=\"{classes}\">");
            body.Append(items[i].Html);
            body.Append("</div>");
        }

        var fullHtml = $"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8" /><style>{DefaultExportCss}</style></head>
            <body>{body}</body></html>
            """;

#if WINDOWS
        await Platforms.Windows.PdfExporter.ExportAsync(fullHtml, outputPath);
#else
        throw new PlatformNotSupportedException("PDF export is only supported on Windows.");
#endif
    }

    private async Task ExportEpubAsync(Wahlberg.Models.MarkdownDocument doc, ExportOptions options, List<ExportSection> sections, List<HeadingInfo> allHeadings, string outputPath)
    {
        var title = Path.GetFileNameWithoutExtension(doc.FilePath);

        byte[]? coverBytes = null;
        string? coverMime = null;
        string? coverExt = null;
        if (options.CoverPage == CoverPageMode.ExternalImage &&
            !string.IsNullOrEmpty(options.CoverImagePath) && File.Exists(options.CoverImagePath))
        {
            coverBytes = await File.ReadAllBytesAsync(options.CoverImagePath);
            coverExt = Path.GetExtension(options.CoverImagePath);
            coverMime = MimeTypeFor(coverExt);
        }

        static string ChapterFile(int i) => $"chapter{i}.xhtml";
        string HrefFor(HeadingInfo h) => $"{ChapterFile(FindSectionIndexForHeadingId(sections, h.Id))}#{h.Id}";

        var manifestItems = new List<(string Id, string Href, string MediaType, string? Properties)>();
        var spineIds = new List<string>();

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mimeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var mw = new StreamWriter(mimeEntry.Open())) mw.Write("application/epub+zip");

            WriteEntry(zip, "META-INF/container.xml", ContainerXml);
            WriteEntry(zip, "OEBPS/styles.css", EpubCss);
            manifestItems.Add(("styles", "styles.css", "text/css", null));

            var startIndex = 0;
            if (coverBytes is not null)
            {
                WriteBinaryEntry(zip, $"OEBPS/cover{coverExt}", coverBytes);
                WriteEntry(zip, "OEBPS/cover.xhtml", BuildCoverXhtml($"cover{coverExt}"));
                manifestItems.Add(("cover-image", $"cover{coverExt}", coverMime!, "cover-image"));
                manifestItems.Add(("cover", "cover.xhtml", "application/xhtml+xml", null));
                spineIds.Add("cover");
            }
            else if (options.CoverPage == CoverPageMode.FirstSection && sections.Count > 0)
            {
                WriteEntry(zip, "OEBPS/" + ChapterFile(0), WrapXhtml(sections[0].Html, sections[0].Heading?.Text ?? title));
                manifestItems.Add(("chapter0", ChapterFile(0), "application/xhtml+xml", null));
                spineIds.Add("chapter0");
                startIndex = 1;
            }

            if (options.IncludeToc)
            {
                var tocHtml = BuildTocHtml(allHeadings, options.TocHeadingLevels, HrefFor);
                WriteEntry(zip, "OEBPS/nav.xhtml", BuildNavXhtml(tocHtml));
                manifestItems.Add(("nav", "nav.xhtml", "application/xhtml+xml", "nav"));
                spineIds.Add("nav");
            }

            for (var i = startIndex; i < sections.Count; i++)
            {
                WriteEntry(zip, "OEBPS/" + ChapterFile(i), WrapXhtml(sections[i].Html, sections[i].Heading?.Text ?? $"Section {i + 1}"));
                manifestItems.Add(($"chapter{i}", ChapterFile(i), "application/xhtml+xml", null));
                spineIds.Add($"chapter{i}");
            }

            WriteEntry(zip, "OEBPS/content.opf", BuildOpf(title, manifestItems, spineIds));
        }

        stream.Position = 0;
        await using var fileStream = File.Create(outputPath);
        await stream.CopyToAsync(fileStream);
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteBinaryEntry(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    private static string WrapXhtml(string bodyHtml, string title) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><meta charset="utf-8" /><title>{System.Net.WebUtility.HtmlEncode(title)}</title><link rel="stylesheet" type="text/css" href="styles.css" /></head>
        <body>
        {SelfCloseVoidElements(bodyHtml)}
        </body>
        </html>
        """;

    private static string BuildNavXhtml(string tocHtml) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
        <head><meta charset="utf-8" /><title>Table of Contents</title><link rel="stylesheet" type="text/css" href="styles.css" /></head>
        <body>
        <nav epub:type="toc" id="toc">
        {SelfCloseVoidElements(tocHtml)}
        </nav>
        </body>
        </html>
        """;

    private const string EpubCss = """
        body { font-family: serif; line-height: 1.5; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border: 1px solid #999; padding: 6px 10px; }
        pre { background: #f4f4f4; padding: 10px; overflow-x: auto; }
        code { font-family: monospace; }
        nav ol { list-style: none; padding-left: 0; }
        nav .toc-level-2 { padding-left: 1em; }
        nav .toc-level-3 { padding-left: 2em; }
        nav .toc-level-4 { padding-left: 3em; }
        nav .toc-level-5 { padding-left: 4em; }
        nav .toc-level-6 { padding-left: 5em; }
        """;

    private static string BuildCoverXhtml(string imageHref) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><meta charset="utf-8" /><title>Cover</title></head>
        <body style="margin:0;text-align:center;">
        <img src="{imageHref}" alt="Cover" style="max-width:100%;height:100%;" />
        </body>
        </html>
        """;

    private const string ContainerXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private static string BuildOpf(string title, List<(string Id, string Href, string MediaType, string? Properties)> items, List<string> spineIds)
    {
        var manifest = new StringBuilder();
        foreach (var item in items)
        {
            var props = item.Properties is null ? "" : $" properties=\"{item.Properties}\"";
            manifest.Append($"<item id=\"{item.Id}\" href=\"{item.Href}\" media-type=\"{item.MediaType}\"{props}/>");
        }

        var spine = new StringBuilder();
        foreach (var id in spineIds)
            spine.Append($"<itemref idref=\"{id}\"/>");

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="bookid">urn:uuid:{Guid.NewGuid()}</dc:identifier>
                <dc:title>{System.Net.WebUtility.HtmlEncode(title)}</dc:title>
                <dc:language>en</dc:language>
              </metadata>
              <manifest>{manifest}</manifest>
              <spine>{spine}</spine>
            </package>
            """;
    }
}
