namespace Wahlberg.Models;

public enum ExportFormat { Pdf, Epub, EmbeddedMarkdown }

public enum CoverPageMode { None, FirstSection, ExternalImage }

public class ExportOptions
{
    public ExportFormat Format { get; set; } = ExportFormat.Pdf;
    public bool IncludeToc { get; set; } = true;
    public HashSet<int> TocHeadingLevels { get; set; } = [1, 2, 3];
    public bool SplitOnHorizontalRule { get; set; }
    public int? SplitAtHeadingLevel { get; set; }
    public CoverPageMode CoverPage { get; set; } = CoverPageMode.None;
    public string? CoverImagePath { get; set; }
}
