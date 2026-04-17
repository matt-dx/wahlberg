namespace Wahlberg.Models;

public class ViewerTheme
{
    public string Name { get; set; } = "default";

    // Main colors
    public string BackgroundColor { get; set; } = "#1e1e1e";
    public string SurfaceColor { get; set; } = "#252526";
    public string TextColor { get; set; } = "#d4d4d4";
    public string HeadingColor { get; set; } = "#e0e0e0";
    public string AccentColor { get; set; } = "#0078d4";
    public string BorderColor { get; set; } = "#333333";
    public string LinkColor { get; set; } = "#4ea8db";

    // Code
    public string CodeColor { get; set; } = "#ce9178";
    public string CodeBackground { get; set; } = "#2d2d2d";
    public string PreBackground { get; set; } = "#1a1a2e";

    // Table
    public string TableHeaderBackground { get; set; } = "#2d2d2d";
    public string TableRowAltBackground { get; set; } = "#1a1a1a";
    public string TableBorderColor { get; set; } = "#444444";

    // Fonts
    public string FontFamily { get; set; } = "-apple-system, BlinkMacSystemFont, \"Segoe UI\", Roboto, sans-serif";
    public string CodeFontFamily { get; set; } = "\"Cascadia Code\", \"Fira Code\", Consolas, monospace";
    public int FontSizePx { get; set; } = 15;

    public ViewerTheme Clone() => new()
    {
        Name = Name,
        BackgroundColor = BackgroundColor,
        SurfaceColor = SurfaceColor,
        TextColor = TextColor,
        HeadingColor = HeadingColor,
        AccentColor = AccentColor,
        BorderColor = BorderColor,
        LinkColor = LinkColor,
        CodeColor = CodeColor,
        CodeBackground = CodeBackground,
        PreBackground = PreBackground,
        TableHeaderBackground = TableHeaderBackground,
        TableRowAltBackground = TableRowAltBackground,
        TableBorderColor = TableBorderColor,
        FontFamily = FontFamily,
        CodeFontFamily = CodeFontFamily,
        FontSizePx = FontSizePx
    };
}
