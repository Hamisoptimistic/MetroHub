using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private static readonly string[] Palette = { "#2563EB", "#7C3AED", "#059669", "#D97706", "#DC2626" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsSummary))]
    private string _label = "Stub Widget";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsSummary))]
    private string _boxColor = "#2563EB";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsSummary))]
    private int _counter = 0;

    public string SettingsSummary => $"Color: {BoxColor} | Clicks: {Counter}";

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
        if (settings != null)
        {
            Label = settings.Label ?? "Stub Widget";
            BoxColor = string.IsNullOrWhiteSpace(settings.BoxColor) ? "#2563EB" : settings.BoxColor;
            Counter = settings.Counter;
        }
    }

    public override void SaveSettings()
    {
        var settings = new StubWidgetSettings
        {
            Label = Label,
            BoxColor = BoxColor,
            Counter = Counter
        };
        Model.TargetPath = "stub";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
        MainWindow.Current?.SaveGroupsAndLayout();
    }

    [RelayCommand]
    public void CycleColor()
    {
        Counter++;
        BoxColor = Palette[Counter % Palette.Length];
        Label = $"Stub Widget #{Counter}";
        SaveSettings();
    }
}


