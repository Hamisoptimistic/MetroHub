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
