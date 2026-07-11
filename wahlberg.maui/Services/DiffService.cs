using System.Net;
using System.Text;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Wahlberg.Services;

public class DiffService
{
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
}
