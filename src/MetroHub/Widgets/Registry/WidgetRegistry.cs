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
    private static WidgetDefinition[] _allCached = Array.Empty<WidgetDefinition>();

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
            Factory: model => new StubWidgetViewModel(model),
            Category: "Lifestyle"
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
            Factory: model => new MetroHub.Widgets.Catalog.Clock.ClockWidgetViewModel(model),
            Category: "Productivity"
        ));

        // Register Calendar widget (month grid at 8x6, today's date card at 4x4)
        Register(new WidgetDefinition(
            Id: "calendar",
            DisplayName: "Calendar",
            Description: "Windows 10 style monthly calendar, with a compact today's date card at 4x4",
            Icon: SymbolRegular.CalendarLtr24,
            AllowedSizes: new[]
            {
                WidgetSize.Large, // 4x4 (248x248px) today's date card
                WidgetSize.Huge   // 8x6 (504x376px) month grid (default)
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Calendar.CalendarWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Calendar.CalendarWidgetView),
            DefaultSize: WidgetSize.Huge,
            Factory: model => new MetroHub.Widgets.Catalog.Calendar.CalendarWidgetViewModel(model),
            Category: "Productivity"
        ));

        // Register Media Player widget (Groove Live Tile style)
        Register(new WidgetDefinition(
            Id: "media",
            DisplayName: "Media Player",
            Description: "Live media player with playback controls and track details",
            Icon: SymbolRegular.Play24,
            AllowedSizes: new[]
            {
                WidgetSize.SlimWide,      // 4x1
                WidgetSize.ExtraWide,     // 6x2
                WidgetSize.PortraitLarge, // 4x6 (Zune Handheld)
                WidgetSize.Banner3,       // 8x3
                WidgetSize.Mega           // 8x4
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Media.MediaWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Media.MediaWidgetView),
            DefaultSize: WidgetSize.Mega,
            Factory: model => new MetroHub.Widgets.Catalog.Media.MediaWidgetViewModel(model),
            Category: "Sound"
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
            Factory: model => new MetroHub.Widgets.Catalog.Pomodoro.PomodoroWidgetViewModel(model),
            Category: "Productivity"
        ));

        // Register Photo Stream widget
        Register(new WidgetDefinition(
            Id: "photos",
            DisplayName: "Photo Stream",
            Description: "Displays a live shuffling stream of your favorite photos",
            Icon: SymbolRegular.Image24,
            AllowedSizes: new[]
            {
                WidgetSize.Large,   // 4x4
                WidgetSize.Banner3, // 8x3
                WidgetSize.Mega     // 8x4
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Photos.PhotosWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Photos.PhotosWidgetView),
            DefaultSize: WidgetSize.Large,
            Factory: model => new MetroHub.Widgets.Catalog.Photos.PhotosWidgetViewModel(model),
            Category: "Lifestyle"
        ));

        // Register Audio Controls widget
        Register(new WidgetDefinition(
            Id: "audio_controls",
            DisplayName: "Audio Controls",
            Description: "Master volume slider, output device switcher, and per-app audio mixer",
            Icon: SymbolRegular.Speaker224,
            AllowedSizes: new[]
            {
                WidgetSize.SlimWide,   // 4x1
                WidgetSize.SlimBanner, // 8x1
                WidgetSize.Mega,       // 8x4
                WidgetSize.Huge        // 8x6
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.AudioControls.AudioControlsWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.AudioControls.AudioControlsWidgetView),
            DefaultSize: WidgetSize.Mega,
            Factory: model => new MetroHub.Widgets.Catalog.AudioControls.AudioControlsWidgetViewModel(model),
            Category: "Sound"
        ));

        // Register Focus Radio & Ambient Sounds widget
        Register(new WidgetDefinition(
            Id: "radio",
            DisplayName: "Focus Radio",
            Description: "Curated ambient, nature, lo-fi, and coding radio stations for deep focus",
            Icon: SymbolRegular.HeadphonesSoundWave24,
            AllowedSizes: new[]
            {
                WidgetSize.Huge // 8x6 (504x376px)
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Radio.RadioWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Radio.RadioWidgetView),
            DefaultSize: WidgetSize.Huge,
            Factory: model => new MetroHub.Widgets.Catalog.Radio.RadioWidgetViewModel(model),
            Category: "Sound"
        ));

        // Register Notes & Tasks (Notepad / Todo) widget
        Register(new WidgetDefinition(
            Id: "notepad",
            DisplayName: "Notes & Tasks",
            Description: "Quick text notepad with bullet lists, numbered lists, and interactive to-do checklists",
            Icon: SymbolRegular.Notepad24,
            AllowedSizes: new[]
            {
                WidgetSize.Mega,   // 8x4
                WidgetSize.Huge,   // 8x6
                WidgetSize.Canvas, // 8x8
                WidgetSize.Full    // 8x10
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Notepad.NotepadWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Notepad.NotepadWidgetView),
            DefaultSize: WidgetSize.Mega,
            Factory: model => new MetroHub.Widgets.Catalog.Notepad.NotepadWidgetViewModel(model),
            Category: "Productivity"
        ));

        // Register Network & Internet widget
        Register(new WidgetDefinition(
            Id: "network",
            DisplayName: "Network & Internet",
            Description: "Real-time Ethernet, Wi-Fi, live bandwidth telemetry, and diagnostics",
            Icon: SymbolRegular.Globe24,
            AllowedSizes: new[]
            {
                WidgetSize.Huge // 8x6 (Default size)
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Network.NetworkWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Network.NetworkWidgetView),
            DefaultSize: WidgetSize.Huge,
            Factory: model => new MetroHub.Widgets.Catalog.Network.NetworkWidgetViewModel(model),
            Category: "Lifestyle"
        ));

        // Register Brightness Controls widget
        Register(new WidgetDefinition(
            Id: "brightness_controls",
            DisplayName: "Brightness Controls",
            Description: "Multi-monitor hardware brightness control, DDC/CI sync, and Day/Night profiles",
            Icon: SymbolRegular.BrightnessHigh24,
            AllowedSizes: new[]
            {
                WidgetSize.SlimWide,   // 4x1
                WidgetSize.SlimBanner, // 8x1
                WidgetSize.Mega        // 8x4
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.BrightnessControls.BrightnessControlsWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.BrightnessControls.BrightnessControlsWidgetView),
            DefaultSize: WidgetSize.Mega,
            Factory: model => new MetroHub.Widgets.Catalog.BrightnessControls.BrightnessControlsWidgetViewModel(model),
            Category: "Display"
        ));

        // Register Power widget
        Register(new WidgetDefinition(
            Id: "power",
            DisplayName: "Power",
            Description: "Quick Lock, Sleep, Restart, and Shut Down tiles",
            Icon: SymbolRegular.Power28,
            AllowedSizes: new[]
            {
                WidgetSize.Banner,     // 8x2
                WidgetSize.SlimBanner  // 8x1
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Power.PowerWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Power.PowerWidgetView),
            DefaultSize: WidgetSize.Banner,
            Factory: model => new MetroHub.Widgets.Catalog.Power.PowerWidgetViewModel(model),
            Category: "Quick Actions"
        ));

        // Register Weather & AQI widget
        Register(new WidgetDefinition(
            Id: "weather",
            DisplayName: "Weather & Air Quality",
            Description: "Live weather forecast, temperature, hourly conditions and Air Quality Index",
            Icon: SymbolRegular.WeatherPartlyCloudyDay24,
            AllowedSizes: new[]
            {
                WidgetSize.Medium,    // 2x2
                WidgetSize.Wide,      // 4x2
                WidgetSize.ExtraWide, // 6x2
                WidgetSize.Banner,    // 8x2
                WidgetSize.Large      // 4x4
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Weather.WeatherWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Weather.WeatherWidgetView),
            DefaultSize: WidgetSize.Wide,
            Factory: model => new MetroHub.Widgets.Catalog.Weather.WeatherWidgetViewModel(model),
            Category: "Lifestyle"
        ));

        // Register Daily Quotes widget
        Register(new WidgetDefinition(
            Id: "quotes",
            DisplayName: "Daily Quotes",
            Description: "Daily inspirational quotes and wisdom from timeless thinkers and historical figures",
            Icon: SymbolRegular.TextQuote24,
            AllowedSizes: new[]
            {
                WidgetSize.Banner3  // 8x3 only
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Quotes.QuotesWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Quotes.QuotesWidgetView),
            DefaultSize: WidgetSize.Banner3,
            Factory: model => new MetroHub.Widgets.Catalog.Quotes.QuotesWidgetViewModel(model),
            Category: "Lifestyle"
        ));

        // Register Habit Tracker widget
        Register(new WidgetDefinition(
            Id: "habit",
            DisplayName: "Habit Tracker",
            Description: "Dedicated single-habit monthly streak matrix and daily accountability tracker",
            Icon: SymbolRegular.TargetArrow24,
            AllowedSizes: new[]
            {
                WidgetSize.Huge // 8x6
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Habit.HabitWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Habit.HabitWidgetView),
            DefaultSize: WidgetSize.Huge,
            Factory: model => new MetroHub.Widgets.Catalog.Habit.HabitWidgetViewModel(model),
            Category: "Productivity"
        ));

        // Register Chrome Dino Endless Runner widget
        Register(new WidgetDefinition(
            Id: "dino",
            DisplayName: "T-Rex Runner",
            Description: "Classic Chrome Dino endless runner game with retro monochrome aesthetics",
            Icon: SymbolRegular.Games24,
            AllowedSizes: new[]
            {
                WidgetSize.Banner3 // 8x3 only (504x184 px)
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Dino.DinoWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Dino.DinoWidgetView),
            DefaultSize: WidgetSize.Banner3,
            Factory: model => new MetroHub.Widgets.Catalog.Dino.DinoWidgetViewModel(model),
            Category: "Lifestyle"
        ));

        // Register Caffeine & Sleep widget
        Register(new WidgetDefinition(
            Id: "caffeine_sleep",
            DisplayName: "Caffeine & Sleep",
            Description: "Keep system/display awake and adjust display warmth/night light",
            Icon: SymbolRegular.DrinkCoffee24,
            AllowedSizes: new[]
            {
                WidgetSize.Banner3, // 8x3 (504x184 px)
                WidgetSize.Mega     // 8x4
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.CaffeineSleep.CaffeineSleepWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.CaffeineSleep.CaffeineSleepWidgetView),
            DefaultSize: WidgetSize.Banner3,
            Factory: model => new MetroHub.Widgets.Catalog.CaffeineSleep.CaffeineSleepWidgetViewModel(model),
            Category: "Display"
        ));

        // Register Quick Controls (3-in-1 Media, Volume, Brightness) widget
        Register(new WidgetDefinition(
            Id: "quick_controls",
            DisplayName: "Quick Controls",
            Description: "Unified master media playback, volume, and screen brightness deck",
            Icon: SymbolRegular.SlideSize24,
            AllowedSizes: new[]
            {
                WidgetSize.Wide3,      // 4x3
                WidgetSize.ExtraWide3, // 6x3
                WidgetSize.Banner3     // 8x3
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.QuickControls.QuickControlsWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.QuickControls.QuickControlsWidgetView),
            DefaultSize: WidgetSize.Wide3,
            Factory: model => new MetroHub.Widgets.Catalog.QuickControls.QuickControlsWidgetViewModel(model),
            Category: "Quick Actions"
        ));

        // Register Rover (Windows XP Dog Companion) widget
        Register(new WidgetDefinition(
            Id: "rover",
            DisplayName: "Rover (Windows XP)",
            Description: "Iconic Windows XP animated dog companion with interactive tricks, speech bubbles, and quick search",
            Icon: SymbolRegular.AnimalDog24,
            AllowedSizes: new[]
            {
                WidgetSize.Wide     // 4x2: Centered Desktop Companion
            },
            ViewModelType: typeof(MetroHub.Widgets.Catalog.Rover.RoverWidgetViewModel),
            ViewType: typeof(MetroHub.Widgets.Catalog.Rover.RoverWidgetView),
            DefaultSize: WidgetSize.Wide,
            Factory: model => new MetroHub.Widgets.Catalog.Rover.RoverWidgetViewModel(model),
            Category: "Lifestyle"
        ));
    }

    public static void Register(WidgetDefinition definition)
    {
        _registry[definition.Id] = definition;
        _allCached = _registry.Values.ToArray();
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

    public static IReadOnlyList<WidgetDefinition> GetAll() => _allCached;

    public static IWidgetViewModel CreateViewModelForTile(TileModel tile)
    {
        string widgetId = !string.IsNullOrWhiteSpace(tile.TargetPath) ? tile.TargetPath : "stub";
        if (string.Equals(widgetId, "zune", StringComparison.OrdinalIgnoreCase))
        {
            tile.TargetPath = "media";
            widgetId = "media";
        }
        if (string.Equals(widgetId, "volume", StringComparison.OrdinalIgnoreCase))
        {
            tile.TargetPath = "audio_controls";
            widgetId = "audio_controls";
        }
        if (string.Equals(widgetId, "brightness", StringComparison.OrdinalIgnoreCase))
        {
            tile.TargetPath = "brightness_controls";
            widgetId = "brightness_controls";
        }
        if (string.Equals(widgetId, "caffeine", StringComparison.OrdinalIgnoreCase))
        {
            tile.TargetPath = "caffeine_sleep";
            widgetId = "caffeine_sleep";
        }
        if (TryGet(widgetId, out var def) && def != null)
        {
            return def.CreateViewModel(tile);
        }

        return new StubWidgetViewModel(tile);
    }
}
