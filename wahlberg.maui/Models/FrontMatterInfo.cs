namespace Wahlberg.Models;

public class FrontMatterInfo
{
    public required FrontMatterLanguage Language { get; init; }
    public required string Raw { get; init; }
    public required string HighlightedHtml { get; init; }
}
