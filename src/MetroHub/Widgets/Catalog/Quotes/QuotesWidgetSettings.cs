namespace MetroHub.Widgets.Catalog.Quotes;

/// <summary>
/// Curated font family choices for the Quotes widget.
/// </summary>
public enum QuoteFontFamilyChoice
{
    Merriweather,       // Merriweather (Google Font) - Default
    Quintessential,     // Quintessential (Google Font)
    Georgia,            // Georgia
    Palatino,           // Palatino Linotype
    SegoeUI             // Segoe UI Variable
}

/// <summary>
/// Font style options for the Quotes widget.
/// </summary>
public enum QuoteFontStyleChoice
{
    Italic,
    Regular
}

/// <summary>
/// Persisted state settings for the Quotes widget.
/// </summary>
public class QuotesWidgetSettings
{
    public int LastYear { get; set; } = -1;
    public int LastDayOfYear { get; set; } = -1;
    public int CurrentQuoteIndex { get; set; } = -1;
    public QuoteFontFamilyChoice Font { get; set; } = QuoteFontFamilyChoice.Merriweather;
    public QuoteFontStyleChoice Style { get; set; } = QuoteFontStyleChoice.Italic;
}
