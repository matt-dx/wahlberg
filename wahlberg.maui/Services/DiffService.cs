using System.Net;
using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DiffPlex.Model;
using Markdig;
using Markdig.Renderers;

namespace Wahlberg.Services;

public class DiffService
{
    private static readonly char BlockDelimiter = (char)1;

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public string BuildUnifiedHtml(string leftText, string rightText)
    {
        var diff = InlineDiffBuilder.Diff(leftText, rightText);
        var sb = new StringBuilder();

        foreach (var line in diff.Lines)
        {
            var cssClass = line.Type switch
            {
                ChangeType.Inserted => "diff-line-added",
                ChangeType.Deleted => "diff-line-removed",
                _ => "diff-line-unchanged"
            };
            var marker = line.Type switch
            {
                ChangeType.Inserted => "+",
                ChangeType.Deleted => "-",
                _ => " "
            };
            var lineNumber = line.Position?.ToString() ?? "";

            sb.Append("<div class=\"diff-line ").Append(cssClass).Append("\">")
              .Append("<span class=\"diff-line-num\">").Append(lineNumber).Append("</span>")
              .Append("<span class=\"diff-line-marker\">").Append(marker).Append("</span>")
              .Append("<span class=\"diff-line-text\">").Append(WebUtility.HtmlEncode(line.Text)).Append("</span>")
              .Append("</div>");
        }

        return sb.ToString();
    }

    public string BuildSideBySideHtml(string leftText, string rightText)
    {
        var diff = SideBySideDiffBuilder.Diff(leftText, rightText);
        var sb = new StringBuilder();

        sb.Append("<div class=\"diff-side-by-side\">");
        sb.Append("<div class=\"diff-side\">").Append(RenderSideLines(diff.OldText.Lines, isOldSide: true)).Append("</div>");
        sb.Append("<div class=\"diff-side\">").Append(RenderSideLines(diff.NewText.Lines, isOldSide: false)).Append("</div>");
        sb.Append("</div>");

        return sb.ToString();
    }

    private static string RenderSideLines(System.Collections.Generic.List<DiffPiece> lines, bool isOldSide)
    {
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            var cssClass = line.Type switch
            {
                ChangeType.Deleted or ChangeType.Modified when isOldSide => "diff-line-removed",
                ChangeType.Inserted or ChangeType.Modified when !isOldSide => "diff-line-added",
                ChangeType.Imaginary => "diff-line-imaginary",
                _ => "diff-line-unchanged"
            };
            var lineNumber = line.Position?.ToString() ?? "";
            var text = line.Type == ChangeType.Imaginary ? "" : WebUtility.HtmlEncode(line.Text);

            sb.Append("<div class=\"diff-line ").Append(cssClass).Append("\">")
              .Append("<span class=\"diff-line-num\">").Append(lineNumber).Append("</span>")
              .Append("<span class=\"diff-line-text\">").Append(text).Append("</span>")
              .Append("</div>");
        }

        return sb.ToString();
    }

    public string BuildUnifiedDiffText(string leftLabel, string leftText, string rightLabel, string rightText)
    {
        var diff = InlineDiffBuilder.Diff(leftText, rightText);
        var sb = new StringBuilder();

        sb.Append("--- ").Append(leftLabel).Append('\n');
        sb.Append("+++ ").Append(rightLabel).Append('\n');

        foreach (var line in diff.Lines)
        {
            var marker = line.Type switch
            {
                ChangeType.Inserted => '+',
                ChangeType.Deleted => '-',
                _ => ' '
            };
            sb.Append(marker).Append(line.Text).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Diffs at markdown-block granularity (paragraphs, headings, lists, tables, fenced code, etc. each
    /// as one atomic unit) and renders each block through Markdig, so the result looks like the normal
    /// viewer with added/removed blocks marked — as opposed to <see cref="BuildUnifiedHtml"/>, which diffs
    /// raw source text line-by-line.
    /// </summary>
    public string BuildRenderedUnifiedHtml(string leftText, string rightText, string? leftDir, string? rightDir)
    {
        var leftBlocks = GetTopLevelBlocks(leftText, leftDir);
        var rightBlocks = GetTopLevelBlocks(rightText, rightDir);
        var diffResult = ComputeBlockDiff(leftBlocks, rightBlocks);

        var sb = new StringBuilder();
        var oldIdx = 0;

        foreach (var block in diffResult.DiffBlocks.OrderBy(b => b.DeleteStartA))
        {
            while (oldIdx < block.DeleteStartA)
            {
                sb.Append("<div class=\"diff-block diff-block-unchanged\">").Append(leftBlocks[oldIdx].Html).Append("</div>");
                oldIdx++;
            }

            for (var i = 0; i < block.DeleteCountA; i++)
                sb.Append("<div class=\"diff-block diff-block-removed\">").Append(leftBlocks[oldIdx + i].Html).Append("</div>");
            for (var i = 0; i < block.InsertCountB; i++)
                sb.Append("<div class=\"diff-block diff-block-added\">").Append(rightBlocks[block.InsertStartB + i].Html).Append("</div>");

            oldIdx += block.DeleteCountA;
        }

        while (oldIdx < leftBlocks.Count)
        {
            sb.Append("<div class=\"diff-block diff-block-unchanged\">").Append(leftBlocks[oldIdx].Html).Append("</div>");
            oldIdx++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Side-by-side counterpart of <see cref="BuildRenderedUnifiedHtml"/> — same block-level diff,
    /// rendered as two aligned columns (padding with an empty "imaginary" block on whichever side
    /// lacks a corresponding block), mirroring <see cref="BuildSideBySideHtml"/>'s layout.
    /// </summary>
    public string BuildRenderedSideBySideHtml(string leftText, string rightText, string? leftDir, string? rightDir)
    {
        var leftBlocks = GetTopLevelBlocks(leftText, leftDir);
        var rightBlocks = GetTopLevelBlocks(rightText, rightDir);
        var diffResult = ComputeBlockDiff(leftBlocks, rightBlocks);

        var leftSb = new StringBuilder();
        var rightSb = new StringBuilder();
        var oldIdx = 0;
        var newIdx = 0;

        foreach (var block in diffResult.DiffBlocks.OrderBy(b => b.DeleteStartA))
        {
            while (oldIdx < block.DeleteStartA)
            {
                leftSb.Append("<div class=\"diff-block diff-block-unchanged\">").Append(leftBlocks[oldIdx].Html).Append("</div>");
                rightSb.Append("<div class=\"diff-block diff-block-unchanged\">").Append(rightBlocks[newIdx].Html).Append("</div>");
                oldIdx++;
                newIdx++;
            }

            var pairCount = Math.Max(block.DeleteCountA, block.InsertCountB);
            for (var i = 0; i < pairCount; i++)
            {
                leftSb.Append(i < block.DeleteCountA
                    ? $"<div class=\"diff-block diff-block-removed\">{leftBlocks[oldIdx + i].Html}</div>"
                    : "<div class=\"diff-block diff-block-imaginary\"></div>");
                rightSb.Append(i < block.InsertCountB
                    ? $"<div class=\"diff-block diff-block-added\">{rightBlocks[block.InsertStartB + i].Html}</div>"
                    : "<div class=\"diff-block diff-block-imaginary\"></div>");
            }

            oldIdx += block.DeleteCountA;
            newIdx += block.InsertCountB;
        }

        while (oldIdx < leftBlocks.Count)
        {
            leftSb.Append("<div class=\"diff-block diff-block-unchanged\">").Append(leftBlocks[oldIdx].Html).Append("</div>");
            rightSb.Append("<div class=\"diff-block diff-block-unchanged\">").Append(rightBlocks[newIdx].Html).Append("</div>");
            oldIdx++;
            newIdx++;
        }

        var sb = new StringBuilder();
        sb.Append("<div class=\"diff-side-by-side\">");
        sb.Append("<div class=\"diff-side\">").Append(leftSb).Append("</div>");
        sb.Append("<div class=\"diff-side\">").Append(rightSb).Append("</div>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private List<(string Signature, string Html)> GetTopLevelBlocks(string markdown, string? baseDir)
    {
        var document = Markdig.Markdown.Parse(markdown, _pipeline);
        var result = new List<(string, string)>();

        foreach (var block in document)
        {
            var span = block.Span;
            var length = span.End - span.Start + 1;
            var signature = length > 0 && span.Start < markdown.Length
                ? markdown.Substring(span.Start, Math.Min(length, markdown.Length - span.Start))
                : string.Empty;

            using var writer = new StringWriter();
            var renderer = new HtmlRenderer(writer);
            _pipeline.Setup(renderer);
            renderer.Render(block);
            var html = writer.ToString();

            if (!string.IsNullOrEmpty(baseDir) && !AppMode.IsServiceMode)
                html = TabService.ResolveLocalPaths(html, baseDir);

            result.Add((signature, html));
        }

        return result;
    }

    private static DiffResult ComputeBlockDiff(
        List<(string Signature, string Html)> leftBlocks,
        List<(string Signature, string Html)> rightBlocks)
    {
        // Map each distinct block signature to a short numeric token instead of joining the raw
        // signatures directly: a raw signature is an arbitrary markdown substring and could contain
        // BlockDelimiter itself, which would corrupt tokenization on Split. Numeric tokens can't
        // collide with the delimiter and are far smaller than the block text for large documents.
        var tokens = new Dictionary<string, string>();
        string TokenFor(string signature)
        {
            if (!tokens.TryGetValue(signature, out var token))
            {
                token = tokens.Count.ToString();
                tokens[signature] = token;
            }
            return token;
        }

        var oldJoined = string.Join(BlockDelimiter, leftBlocks.Select(b => TokenFor(b.Signature)));
        var newJoined = string.Join(BlockDelimiter, rightBlocks.Select(b => TokenFor(b.Signature)));
        // text.Split on an empty string yields [""] (one token), which would desync from an
        // empty block list (zero blocks) and cause out-of-range indexing when rendering.
        return new Differ().CreateCustomDiffs(oldJoined, newJoined, false,
            text => text.Length == 0 ? [] : text.Split(BlockDelimiter));
    }
}
