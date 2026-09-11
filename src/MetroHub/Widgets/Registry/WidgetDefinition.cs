using System;
using System.Collections.Generic;
using MetroHub.Core.Models;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Registry;

/// <summary>
/// Declarative widget definition record (Phase 0.6).
/// Registers a widget's identity, metadata, allowed grid sizes, ViewModel type, and optional factory.
/// </summary>
public record WidgetDefinition(
    string Id,
    string DisplayName,
    string Description,
    SymbolRegular Icon,
    IReadOnlyList<WidgetSize> AllowedSizes,
    Type ViewModelType,
    Type? ViewType = null,
    WidgetSize? DefaultSize = null,
    Func<TileModel, IWidgetViewModel>? Factory = null
)
{
    public WidgetSize InitialSize => DefaultSize ?? (AllowedSizes.Count > 0 ? AllowedSizes[0] : WidgetSize.Medium);

    public IWidgetViewModel CreateViewModel(TileModel model)
    {
        if (Factory != null)
        {
            var vm = Factory(model);
            vm.Initialize(model);
            return vm;
        }

        var instance = (IWidgetViewModel)Activator.CreateInstance(ViewModelType, model)!;
        instance.Initialize(model);
        return instance;
    }
}
