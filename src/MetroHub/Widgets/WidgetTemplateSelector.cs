using System;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using MetroHub.Widgets.Registry;

namespace MetroHub.Widgets;

/// <summary>
/// Dynamically resolves DataTemplates for widget ViewModels using declarations in WidgetRegistry.
/// Eliminates the need to declare hardcoded DataTemplates in TileControl.xaml whenever a new widget is added.
/// </summary>
public class WidgetTemplateSelector : DataTemplateSelector
{
    private static readonly ConcurrentDictionary<Type, DataTemplate> _templateCache = new();

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is IWidgetViewModel vm)
        {
            var vmType = vm.GetType();
            if (_templateCache.TryGetValue(vmType, out var cachedTemplate))
            {
                return cachedTemplate;
            }

            if (WidgetRegistry.TryGetByViewModelType(vmType, out var def) && def?.ViewType != null)
            {
                var factory = new FrameworkElementFactory(def.ViewType);
                var template = new DataTemplate(vmType) { VisualTree = factory };
                template.Seal();
                _templateCache[vmType] = template;
                return template;
            }
        }

        // Return null to fall back to ambient resource DataTemplate lookup (e.g. TileModel for app shortcuts)
        return null;
    }
}
