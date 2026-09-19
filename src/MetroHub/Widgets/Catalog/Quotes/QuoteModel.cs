using System.Text.Json.Serialization;

namespace MetroHub.Widgets.Catalog.Quotes;

/// <summary>
/// Model representing a single quote entry.
/// </summary>
public class QuoteModel
{
    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}
