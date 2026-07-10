namespace Wahlberg.Models;

/// <summary>
/// One content chunk produced by splitting a document on horizontal rules and/or heading boundaries.
/// </summary>
public class ExportSection
{
    public HeadingInfo? Heading { get; init; }
    public required string Html { get; init; }
}
