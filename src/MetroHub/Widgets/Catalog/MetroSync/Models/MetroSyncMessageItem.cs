using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Catalog.MetroSync.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextPayloadItem), "text")]
[JsonDerivedType(typeof(ImagePayloadItem), "image")]
[JsonDerivedType(typeof(FilePayloadItem), "file")]
public abstract partial class MetroSyncMessageItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long UpdateId { get; set; }
    public long MessageId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public bool IsOutgoing { get; set; }

    [JsonIgnore]
    public string TimeDisplay => Timestamp > DateTimeOffset.MinValue
        ? Timestamp.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture)
        : string.Empty;
}
