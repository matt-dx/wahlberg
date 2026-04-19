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
}
