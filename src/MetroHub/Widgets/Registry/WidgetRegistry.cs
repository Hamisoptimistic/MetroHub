using System;
using System.Collections.Generic;
using System.Linq;
using MetroHub.Core.Models;
using MetroHub.Widgets.Catalog.Stub;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Registry;

/// <summary>
/// Declarative widget registry (Phase 0.6).
/// Central registry where all widgets register their definition.
/// Adding a new widget is registering one WidgetDefinition entry.
/// </summary>
public static class WidgetRegistry
{
    private static readonly Dictionary<string, WidgetDefinition> _registry = new(StringComparer.OrdinalIgnoreCase);

    static WidgetRegistry()
    {
        // Register standard Stub widget
        Register(new WidgetDefinition(
            Id: "stub",
            DisplayName: "Blank Widget",
            Description: "Generic widget card placeholder",
            Icon: SymbolRegular.Square24,
            AllowedSizes: new[]
            {
                WidgetSize.Small,
                WidgetSize.Medium,
                WidgetSize.Wide,
                WidgetSize.Tall,
                WidgetSize.Large,
                WidgetSize.Banner
            },
            ViewModelType: typeof(StubWidgetViewModel),
            Factory: model => new StubWidgetViewModel(model)
        ));

        // Register Clock widget
        Register(new WidgetDefinition(
            Id: "clock",
            DisplayName: "Clock",
            Description: "Digital clock with live time and date",
            Icon: SymbolRegular.Clock24,
            AllowedSizes: new[]
            {
                WidgetSize.Wide,      // 4x2
                WidgetSize.Large,     // 4x4
                WidgetSize.LargeWide, // 6x4
                WidgetSize.Banner,    // 8x2
                WidgetSize.Mega       // 8x4
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Clock.ClockWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Clock.ClockWidgetView),
            DefaultSize: WidgetSize.Wide,
            Factory: model => new MetroHub.Widgets.Catalog.Clock.ClockWidgetViewModel(model)
        ));

        // Register Calendar widget
        Register(new WidgetDefinition(
            Id: "calendar",
            DisplayName: "Calendar",
            Description: "Windows 10 style monthly calendar",
            Icon: SymbolRegular.CalendarLtr24,
            AllowedSizes: new[]
            {
                WidgetSize.Huge // 8x6
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Calendar.CalendarWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Calendar.CalendarWidgetView),
            DefaultSize: WidgetSize.Huge,
            Factory: model => new MetroHub.Widgets.Catalog.Calendar.CalendarWidgetViewModel(model)
        ));

        // Register Media Player widget (Groove Live Tile style)
        Register(new WidgetDefinition(
            Id: "media",
            DisplayName: "Media Player",
            Description: "Live media player with playback controls and track details",
            Icon: SymbolRegular.Play24,
            AllowedSizes: new[]
            {
                WidgetSize.Mega,    // 8x4
                WidgetSize.Banner3  // 8x3
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Media.MediaWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Media.MediaWidgetView),
            DefaultSize: WidgetSize.Mega,
            Factory: model => new MetroHub.Widgets.Catalog.Media.MediaWidgetViewModel(model)
        ));

        // Register Focus / Pomodoro widget
        Register(new WidgetDefinition(
            Id: "pomodoro",
            DisplayName: "Focus Timer",
            Description: "Fluent Pomodoro timer with focus sessions, breaks and cycle tracking",
            Icon: SymbolRegular.Timer24,
            AllowedSizes: new[]
            {
                WidgetSize.Wide,    // 4x2
                WidgetSize.Banner3, // 8x3
                WidgetSize.Mega     // 8x4
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Pomodoro.PomodoroWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Pomodoro.PomodoroWidgetView),
            DefaultSize: WidgetSize.Mega,
            Factory: model => new MetroHub.Widgets.Catalog.Pomodoro.PomodoroWidgetViewModel(model)
        ));
    }

    public static void Register(WidgetDefinition definition)
    {
        _registry[definition.Id] = definition;
    }

    public static WidgetDefinition? Get(string id)
    {
        _registry.TryGetValue(id, out var def);
        return def;
    }

    public static bool TryGet(string id, out WidgetDefinition? definition)
    {
        return _registry.TryGetValue(id, out definition);
    }

    public static IReadOnlyList<WidgetDefinition> GetAll() => _registry.Values.ToList();

    public static IWidgetViewModel CreateViewModelForTile(TileModel tile)
    {
        string widgetId = !string.IsNullOrWhiteSpace(tile.TargetPath) ? tile.TargetPath : "stub";
        if (TryGet(widgetId, out var def) && def != null)
        {
            return def.CreateViewModel(tile);
        }

        return new StubWidgetViewModel(tile);
    }
}
