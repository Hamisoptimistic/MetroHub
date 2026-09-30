using System;
using System.Collections.Generic;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Presentation.Themes;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Template;

/// <summary>
/// OFFICIAL BOILERPLATE TEMPLATE: Copy this folder to start a new widget!
/// 
/// This ViewModel showcases standard MetroHub patterns:
/// 1. Lifecycle & Settings: Inherits WidgetViewModelBase for auto-persistence, pause/resume, and heartbeat.
/// 2. Primary Click: Implements IWidgetActionHandler for custom action when clicking the tile.
/// 3. Context Menu: Implements IWidgetContextMenuProvider to add items to the tile's right-click menu.
/// 4. MVVM: Uses CommunityToolkit.Mvvm for [ObservableProperty] and [RelayCommand].
/// </summary>
public sealed partial class TemplateWidgetViewModel : WidgetViewModelBase, IWidgetActionHandler, IWidgetContextMenuProvider
{
    private static readonly string[] Palette = { "#2563EB", "#7C3AED", "#059669", "#D97706", "#DC2626" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsSummary))]
    private string _label = "Template Widget";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsSummary))]
    private string _boxColor = "#2563EB";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsSummary))]
    private int _counter = 0;

    [ObservableProperty]
    private bool _isFeatureEnabled = true;

    public string SettingsSummary => $"Color: {BoxColor} | Clicks: {Counter}";

    /// <summary>
    /// Declare which tile grid sizes your widget supports.
    /// MetroHub's right-click "Resize" submenu will automatically populate from this list!
    /// </summary>
    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new List<WidgetSize>
    {
        WidgetSize.Small,    // 1x1
        WidgetSize.Medium,   // 2x2
        WidgetSize.Wide,     // 4x2
        WidgetSize.Large     // 4x4
    };

    public TemplateWidgetViewModel(TileModel model) : base(model)
    {
        LoadSettings(model.SettingsJson);
    }

    /// <summary>
    /// Called when the widget initializes. Load your saved JSON settings here.
    /// </summary>
    protected override void LoadSettings(string? settingsJson)
    {
        var settings = WidgetSerializer.Deserialize<TemplateWidgetSettings>(settingsJson);
        if (settings != null)
        {
            Label = settings.Label ?? "Template Widget";
            BoxColor = string.IsNullOrWhiteSpace(settings.BoxColor) ? "#2563EB" : settings.BoxColor;
            Counter = settings.Counter;
            IsFeatureEnabled = settings.IsFeatureEnabled;
        }
    }

    /// <summary>
    /// Called whenever settings change to persist state across app restarts.
    /// </summary>
    public override void SaveSettings()
    {
        var settings = new TemplateWidgetSettings
        {
            Label = Label,
            BoxColor = BoxColor,
            Counter = Counter,
            IsFeatureEnabled = IsFeatureEnabled
        };
        Model.TargetPath = "template";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
        NotifySettingsChanged();
    }

    /// <summary>
    /// Primary Action (IWidgetActionHandler): Triggered when the user clicks the tile background.
    /// </summary>
    public void OnPrimaryAction() => CycleColor();

    [RelayCommand]
    public void CycleColor()
    {
        Counter++;
        BoxColor = Palette[Counter % Palette.Length];
        Label = $"Template Widget #{Counter}";
        SaveSettings();
    }

    [RelayCommand]
    public void ResetCounter()
    {
        Counter = 0;
        BoxColor = Palette[0];
        Label = "Template Widget";
        SaveSettings();
    }

    /// <summary>
    /// Context Menu (IWidgetContextMenuProvider): Yields custom menu items to inject into the right-click menu.
    /// MetroHub automatically styles icons, sets menu tags, and adds separators.
    /// </summary>
    public IEnumerable<Control> GetContextMenuItems()
    {
        var resetItem = new MenuItem
        {
            Header = "Reset Template Counter",
            Icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowReset24,
                FontSize = 20,
                Foreground = ThemeTokens.MenuIconForegroundBrush
            }
        };
        resetItem.Click += (s, ev) => ResetCounter();

        yield return resetItem;
    }
}
