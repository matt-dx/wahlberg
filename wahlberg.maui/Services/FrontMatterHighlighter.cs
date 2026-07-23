using System.Globalization;
using System.Net;
using System.Text;
using Wahlberg.Models;

namespace Wahlberg.Services;

/// <summary>
/// Small hand-written tokenizers producing highlighted HTML (<c>&lt;span class="fm-*"&gt;</c>)
/// for the three front-matter formats. No parsing library is involved — these formats are
/// simple enough for a line/char scan, and the goal is display, not deserialization.
/// </summary>
public static class FrontMatterHighlighter
{
    public static string Highlight(string raw, FrontMatterLanguage language) => language switch
    {
        FrontMatterLanguage.Yaml => HighlightYaml(raw),
        FrontMatterLanguage.Toml => HighlightToml(raw),
        FrontMatterLanguage.Json => HighlightJson(raw),
        _ => WebUtility.HtmlEncode(raw)
    };

    private static List<string> SplitLines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    // ===== YAML =====

    private static string HighlightYaml(string raw)
    {
        var sb = new StringBuilder();
        var lines = SplitLines(raw);
        for (var i = 0; i < lines.Count; i++)
        {
            HighlightYamlLine(sb, lines[i]);
            if (i < lines.Count - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void HighlightYamlLine(StringBuilder sb, string line)
    {
        var indentLen = 0;
        while (indentLen < line.Length && line[indentLen] == ' ') indentLen++;
        sb.Append(line, 0, indentLen);
        var rest = line[indentLen..];
        if (rest.Length == 0) return;

        if (rest[0] == '#')
        {
            AppendSpan(sb, "fm-comment", rest);
            return;
        }

        if (rest == "-" || rest.StartsWith("- "))
        {
            var dashLen = rest == "-" ? 1 : 2;
            AppendSpan(sb, "fm-punct", rest[..dashLen]);
            HighlightYamlLine(sb, rest[dashLen..]);
            return;
        }

        var colonIdx = FindTopLevelDelimiter(rest, ':', requireBoundaryAfter: true);
        if (colonIdx >= 0)
        {
            AppendSpan(sb, "fm-key", rest[..colonIdx]);
            AppendSpan(sb, "fm-punct", ":");
            HighlightScalarWithLeadingSpace(sb, rest[(colonIdx + 1)..]);
            return;
        }

        AppendEscaped(sb, rest);
    }

    // ===== TOML =====

    private static string HighlightToml(string raw)
    {
        var sb = new StringBuilder();
        var lines = SplitLines(raw);
        for (var i = 0; i < lines.Count; i++)
        {
            HighlightTomlLine(sb, lines[i]);
            if (i < lines.Count - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void HighlightTomlLine(StringBuilder sb, string line)
    {
        var indentLen = 0;
        while (indentLen < line.Length && line[indentLen] == ' ') indentLen++;
        sb.Append(line, 0, indentLen);
        var rest = line[indentLen..];
        if (rest.Length == 0) return;

        if (rest[0] == '#')
        {
            AppendSpan(sb, "fm-comment", rest);
            return;
        }

        var trimmed = rest.TrimEnd();
        if (trimmed.Length > 1 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            AppendSpan(sb, "fm-section", rest);
            return;
        }

        var eqIdx = FindTopLevelDelimiter(rest, '=', requireBoundaryAfter: false);
        if (eqIdx >= 0)
        {
            AppendSpan(sb, "fm-key", rest[..eqIdx]);
            AppendSpan(sb, "fm-punct", "=");
            HighlightScalarWithLeadingSpace(sb, rest[(eqIdx + 1)..]);
            return;
        }

        AppendEscaped(sb, rest);
    }

    // ===== JSON =====

    private static string HighlightJson(string raw)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];

            if (char.IsWhiteSpace(c))
            {
                var start = i;
                while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
                sb.Append(raw, start, i - start);
                continue;
            }

            if (c == '"')
            {
                var start = i;
                i++;
                while (i < raw.Length)
                {
                    if (raw[i] == '\\' && i + 1 < raw.Length) { i += 2; continue; }
                    if (raw[i] == '"') { i++; break; }
                    i++;
                }
                var token = raw[start..i];

                var j = i;
                while (j < raw.Length && char.IsWhiteSpace(raw[j])) j++;
                var isKey = j < raw.Length && raw[j] == ':';
                AppendSpan(sb, isKey ? "fm-key" : "fm-string", token);
                continue;
            }

            if (c is '{' or '}' or '[' or ']' or ':' or ',')
            {
                AppendSpan(sb, "fm-punct", c.ToString());
                i++;
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < raw.Length && char.IsDigit(raw[i + 1])))
            {
                var start = i;
                i++;
                while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                AppendSpan(sb, "fm-number", raw[start..i]);
                continue;
            }

            if (MatchesKeyword(raw, i, "true") || MatchesKeyword(raw, i, "false") || MatchesKeyword(raw, i, "null"))
            {
                var word = MatchesKeyword(raw, i, "true") ? "true" : MatchesKeyword(raw, i, "false") ? "false" : "null";
                AppendSpan(sb, "fm-boolean", word);
                i += word.Length;
                continue;
            }

            AppendEscaped(sb, c.ToString());
            i++;
        }
        return sb.ToString();
    }

    private static bool MatchesKeyword(string s, int index, string keyword) =>
        index + keyword.Length <= s.Length && string.CompareOrdinal(s, index, keyword, 0, keyword.Length) == 0;

    // ===== Shared scalar-value handling (YAML/TOML) =====

    private static void HighlightScalarWithLeadingSpace(StringBuilder sb, string valuePart)
    {
        var i = 0;
        while (i < valuePart.Length && valuePart[i] == ' ') i++;
        if (i > 0) sb.Append(valuePart, 0, i);
        AppendScalarValue(sb, valuePart[i..]);
    }

    private static void AppendScalarValue(StringBuilder sb, string value)
    {
        if (value.Length == 0) return;

        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            AppendSpan(sb, "fm-string", value);
            return;
        }

        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
        {
            AppendArrayValue(sb, value);
            return;
        }

        switch (value)
        {
            case "true" or "false" or "True" or "False" or "null" or "~":
                AppendSpan(sb, "fm-boolean", value);
                return;
        }

        if (IsNumberLike(value))
        {
            AppendSpan(sb, "fm-number", value);
            return;
        }

        if (value[0] == '#')
        {
            AppendSpan(sb, "fm-comment", value);
            return;
        }

        AppendEscaped(sb, value);
    }

    private static void AppendArrayValue(StringBuilder sb, string value)
    {
        AppendSpan(sb, "fm-punct", "[");
        var inner = value[1..^1];
        var items = SplitTopLevelCommas(inner);
        for (var i = 0; i < items.Count; i++)
        {
            HighlightScalarWithLeadingSpace(sb, items[i]);
            if (i < items.Count - 1) AppendSpan(sb, "fm-punct", ",");
        }
        AppendSpan(sb, "fm-punct", "]");
    }

    private static List<string> SplitTopLevelCommas(string s)
    {
        var items = new List<string>();
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        var start = 0;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inSingle) { if (c == '\'') inSingle = false; continue; }
            if (inDouble) { if (c == '\\') { i++; continue; } if (c == '"') inDouble = false; continue; }

            switch (c)
            {
                case '\'': inSingle = true; break;
                case '"': inDouble = true; break;
                case '[': depth++; break;
                case ']': depth--; break;
                case ',' when depth == 0:
                    items.Add(s[start..i]);
                    start = i + 1;
                    break;
            }
        }
        items.Add(s[start..]);

        return items.Where(x => x.Trim().Length > 0).ToList();
    }

    // Finds the first unquoted delimiter — used for YAML "key:" (where the colon must be
    // followed by a space or end-of-line, so a colon inside a bare URL-like value doesn't
    // get mistaken for a key separator) and TOML "key=" (no such boundary requirement).
    private static int FindTopLevelDelimiter(string s, char delimiter, bool requireBoundaryAfter)
    {
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inSingle) { if (c == '\'') inSingle = false; continue; }
            if (inDouble) { if (c == '\\') { i++; continue; } if (c == '"') inDouble = false; continue; }

            if (c == '\'') { inSingle = true; continue; }
            if (c == '"') { inDouble = true; continue; }

            if (c == delimiter && (!requireBoundaryAfter || i + 1 == s.Length || s[i + 1] == ' '))
                return i;
        }
        return -1;
    }

    private static bool IsNumberLike(string s) =>
        double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);

    private static void AppendSpan(StringBuilder sb, string cssClass, string text)
    {
        if (text.Length == 0) return;
        sb.Append("<span class=\"").Append(cssClass).Append("\">");
        sb.Append(WebUtility.HtmlEncode(text));
        sb.Append("</span>");
    }

    private static void AppendEscaped(StringBuilder sb, string text) => sb.Append(WebUtility.HtmlEncode(text));
}
