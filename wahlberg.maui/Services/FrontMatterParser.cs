using Wahlberg.Models;

namespace Wahlberg.Services;

/// <summary>
/// Detects a leading YAML (<c>---</c>), TOML (<c>+++</c>), or JSON (<c>{...}</c>) front-matter
/// block and splits it off from the Markdown body, so it can be hidden from the rendered
/// document and excluded from non-Markdown exports. Front matter must start at the very first
/// character of the file — no leading blank lines — matching the Jekyll/Hugo convention.
/// </summary>
public static class FrontMatterParser
{
    // Bounds the closing-delimiter search for YAML/TOML — without this, a document that opens
    // with a Markdown thematic break ("---") and later contains an unrelated "---" further down
    // (another thematic break, a setext heading underline, etc.) would have that entire leading
    // section mistaken for front matter. Real front matter blocks are always small, so anything
    // beyond this many lines without a closing delimiter is treated as ordinary Markdown instead.
    private const int MaxFrontMatterLines = 200;

    // includeHighlighting: false skips FrontMatterHighlighter entirely — export callers only
    // need the stripped Body and discard FrontMatterInfo, so there's no reason to pay for
    // tokenizing/allocating highlighted HTML on every PDF/EPUB export.
    public static (string Body, FrontMatterInfo? FrontMatter) Extract(string content, bool includeHighlighting = true)
    {
        if (string.IsNullOrEmpty(content)) return (content, null);

        if (TryExtractDelimited(content, "---", FrontMatterLanguage.Yaml, includeHighlighting, out var yamlBody, out var yamlInfo))
            return (yamlBody, yamlInfo);

        if (TryExtractDelimited(content, "+++", FrontMatterLanguage.Toml, includeHighlighting, out var tomlBody, out var tomlInfo))
            return (tomlBody, tomlInfo);

        if (TryExtractJson(content, includeHighlighting, out var jsonBody, out var jsonInfo))
            return (jsonBody, jsonInfo);

        return (content, null);
    }

    private static bool TryExtractDelimited(
        string content, string delimiter, FrontMatterLanguage language, bool includeHighlighting,
        out string body, out FrontMatterInfo? frontMatter)
    {
        body = content;
        frontMatter = null;

        if (!content.StartsWith(delimiter, StringComparison.Ordinal))
            return false;

        var (firstLineEnd, afterFirstLine) = FindLineEnd(content, delimiter.Length);
        if (afterFirstLine == firstLineEnd) return false; // no line terminator at all — single-line file

        // The opening line must be the delimiter alone (rules out "----" or a "---" that's
        // actually part of some other construct on the same line).
        if (content[..firstLineEnd] != delimiter) return false;

        var searchStart = afterFirstLine;
        for (var lineCount = 0; searchStart <= content.Length && lineCount < MaxFrontMatterLines; lineCount++)
        {
            var (lineEnd, nextStart) = FindLineEnd(content, searchStart);
            var line = content[searchStart..lineEnd];

            if (line == delimiter)
            {
                var raw = TrimTrailingLineEnding(content[afterFirstLine..searchStart]);
                body = content[nextStart..];
                frontMatter = new FrontMatterInfo
                {
                    Language = language,
                    Raw = raw,
                    HighlightedHtml = includeHighlighting ? FrontMatterHighlighter.Highlight(raw, language) : string.Empty
                };
                return true;
            }

            if (nextStart == lineEnd) break; // reached end of file without finding a closing delimiter
            searchStart = nextStart;
        }

        return false;
    }

    // Finds the next line terminator starting at `start`, recognizing "\n", "\r\n", and a lone
    // "\r" (legacy Mac-style) alike, so front-matter detection isn't tied to one newline style.
    // Returns (index the line's text ends at, index the following line starts at) — both equal
    // to content.Length when there's no more terminator (i.e. this is the final, unterminated line).
    private static (int LineEnd, int NextStart) FindLineEnd(string content, int start)
    {
        for (var i = start; i < content.Length; i++)
        {
            if (content[i] == '\n') return (i, i + 1);
            if (content[i] == '\r')
                return (i, i + 1 < content.Length && content[i + 1] == '\n' ? i + 2 : i + 1);
        }
        return (content.Length, content.Length);
    }

    // JSON front matter has no delimiter line of its own — the object literal itself is the
    // block, so the end is found by brace-counting (skipping braces inside string literals)
    // rather than scanning for a marker line.
    private static bool TryExtractJson(string content, bool includeHighlighting, out string body, out FrontMatterInfo? frontMatter)
    {
        body = content;
        frontMatter = null;

        if (content[0] != '{') return false;

        var depth = 0;
        var inString = false;
        var escaped = false;
        var endIndex = -1;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0) endIndex = i;
                    break;
            }

            if (endIndex >= 0) break;
        }

        if (endIndex < 0) return false; // unbalanced — treat as ordinary Markdown, not front matter

        var raw = content[..(endIndex + 1)];

        // Brace-balance alone isn't enough to tell real JSON front matter apart from Markdig's
        // generic-attribute syntax (e.g. a "{#id}" or "{.class}" opening a document) — both
        // balance cleanly. Require the object to actually look like JSON: empty, or its first
        // key is a quoted string.
        if (!LooksLikeJsonObject(raw)) return false;

        var bodyStart = endIndex + 1;
        if (bodyStart < content.Length && content[bodyStart] == '\r') bodyStart++;
        if (bodyStart < content.Length && content[bodyStart] == '\n') bodyStart++;

        body = content[bodyStart..];
        frontMatter = new FrontMatterInfo
        {
            Language = FrontMatterLanguage.Json,
            Raw = raw,
            HighlightedHtml = includeHighlighting ? FrontMatterHighlighter.Highlight(raw, FrontMatterLanguage.Json) : string.Empty
        };
        return true;
    }

    private static bool LooksLikeJsonObject(string raw)
    {
        var inner = raw[1..^1].TrimStart();
        return inner.Length == 0 || inner[0] == '"';
    }

    private static string TrimTrailingLineEnding(string s)
    {
        if (s.EndsWith("\r\n", StringComparison.Ordinal)) return s[..^2];
        if (s.EndsWith("\n", StringComparison.Ordinal) || s.EndsWith("\r", StringComparison.Ordinal)) return s[..^1];
        return s;
    }
}
