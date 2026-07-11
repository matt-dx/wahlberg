namespace Wahlberg.Models;

public class MarkdownDocument
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public required string Content { get; set; }
    public string HtmlContent { get; set; } = string.Empty;
    public List<HeadingInfo> Headings { get; set; } = [];
    public bool IsLoading { get; set; } = false;

    public bool IsDiff { get; init; } = false;
    public string? DiffLeftLabel { get; init; }
    public string? DiffRightLabel { get; init; }
    public string DiffUnifiedHtml { get; init; } = string.Empty;
    public string DiffSideBySideHtml { get; init; } = string.Empty;
    public bool DiffShowSideBySide { get; set; }
}
