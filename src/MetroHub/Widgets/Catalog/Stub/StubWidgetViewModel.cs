using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using MetroHub.Core.Models;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Stub;

/// <summary>
/// Minimal stub ViewModel representing a generic or uninitialized widget tile.
/// Implements WidgetViewModelBase using CommunityToolkit.Mvvm (Phase 0.5 directive).
/// Demonstrates source-generated settings persistence and has zero WPF UI dependencies.
/// </summary>
public partial class StubWidgetViewModel : WidgetViewModelBase
{
    [ObservableProperty]
    private string _statusText = "Stub Widget";

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new List<WidgetSize>
    {
        WidgetSize.Small,    // 1x1
        WidgetSize.Medium,   // 2x2
        WidgetSize.Wide,     // 4x2
        WidgetSize.Tall,     // 2x4 (Vertical)
        WidgetSize.Large,    // 4x4 (Hero Square)
        WidgetSize.Banner    // 8x2 (Full Track Width)
    };

    public StubWidgetViewModel(TileModel model) : base(model)
    {
        LoadSettings(model.SettingsJson);
    }

    protected override void LoadSettings(string? settingsJson)
    {
        var settings = WidgetSerializer.Deserialize<StubWidgetSettings>(settingsJson);
        if (settings != null && !string.IsNullOrWhiteSpace(settings.Note))
        {
            StatusText = settings.Note;
        }
    }

    public override void SaveSettings()
    {
        var settings = new StubWidgetSettings
        {
            Note = StatusText
        };
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
    }
}

