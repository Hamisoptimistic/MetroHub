using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.Radio.Services;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// Interaction logic for the Focus Radio & Ambient Sounds widget view (Huge 8x6, 504x376px).
/// Implements seamless border-to-border 4-column station grid, top WidgetTiles category strip,
/// snappy micro-fade transitions on category change, and bottom Fluent audio player bar.
/// </summary>
public sealed partial class RadioWidgetView : UserControl
{
    private static readonly QuadraticEase FadeEasing = new() { EasingMode = EasingMode.EaseOut };

    private string? _previousCategory;
    private RadioWidgetViewModel? _currentViewModel;
    private bool _isRebuildingCategories;

    public RadioWidgetView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookViewModel(DataContext as RadioWidgetViewModel);
        if (CategoryTiles != null && CategoryTiles.Items.Count == 0)
        {
            RebuildCategories();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnhookViewModel();
        HookViewModel(e.NewValue as RadioWidgetViewModel);
    }

    private void HookViewModel(RadioWidgetViewModel? vm)
    {
        if (vm == null || ReferenceEquals(_currentViewModel, vm)) return;

        UnhookViewModel();
        _currentViewModel = vm;
        _previousCategory = vm.SelectedCategoryId;
        _currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _currentViewModel.CatalogService.CatalogChanged += OnCatalogChanged;
        RebuildCategories();
    }

    private void UnhookViewModel()
    {
        if (_currentViewModel != null)
        {
            _currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _currentViewModel.CatalogService.CatalogChanged -= OnCatalogChanged;
            _currentViewModel = null;
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            RebuildCategories();
        }
        else
        {
            Dispatcher.InvokeAsync(RebuildCategories, DispatcherPriority.Normal)
                .FireAndForget("RadioWidgetView.OnCatalogChanged");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RadioWidgetViewModel.SelectedCategoryId))
        {
            if (_isRebuildingCategories) return;

            if (_currentViewModel != null)
            {
                string? newCategory = _currentViewModel.SelectedCategoryId;
                if (!string.Equals(_previousCategory, newCategory, StringComparison.OrdinalIgnoreCase))
                {
                    AnimateCategoryFade();
                    _previousCategory = newCategory;
                    ScrollActiveCategoryIntoView(newCategory);
                }
            }
        }
    }

    private void RebuildCategories()
    {
        if (CategoryTiles == null) return;

        var catalog = _currentViewModel?.CatalogService.GetCatalog() ?? RadioCatalogService.Instance.GetCatalog();
        var categories = catalog?.Categories ?? (IReadOnlyList<RadioCategory>)Array.Empty<RadioCategory>();
        int count = categories.Count;

        string targetId = _currentViewModel?.SelectedCategoryId 
            ?? (CategoryTiles.SelectedValue as string) 
            ?? (count > 0 ? categories[0].Id : "ambient");

        _isRebuildingCategories = true;
        try
        {
            CategoryTiles.Items.Clear();

            WidgetTile? activeTile = null;

            for (int i = 0; i < count; i++)
            {
                var category = categories[i];
                var tile = new WidgetTile
                {
                    Value = category.Id,
                    Header = category.DisplayName,
                    Symbol = RadioSymbols.ParseSymbol(category.IconSymbol),
                    ToolTip = category.DisplayName
                };
                tile.SetResourceReference(WidgetTile.IndicatorBrushProperty, "TextPrimaryBrush");

                if (string.Equals(category.Id, targetId, StringComparison.OrdinalIgnoreCase))
                {
                    activeTile = tile;
                }

                CategoryTiles.Items.Add(tile);
            }

            CategoryTiles.Width = count <= 4 ? 504.0 : count * 126.0;

            CategoryTiles.SetCurrentValue(Selector.SelectedValueProperty, null);
            CategoryTiles.SetCurrentValue(Selector.SelectedValueProperty, targetId);

            if (count > 4 && activeTile != null)
            {
                activeTile.BringIntoView();
            }

            Dispatcher.InvokeAsync(() =>
            {
                CategoryTiles.UpdateLayout();
                if (count > 4 && activeTile != null)
                {
                    activeTile.BringIntoView();
                }
            }, DispatcherPriority.Loaded).FireAndForget("RadioWidgetView.AlignIndicator");
        }
        finally
        {
            _isRebuildingCategories = false;
        }
    }

    private void ScrollActiveCategoryIntoView(string? categoryId)
    {
        if (CategoryTiles == null || CategoryTiles.Items.Count <= 4 || string.IsNullOrWhiteSpace(categoryId)) return;

        foreach (var item in CategoryTiles.Items)
        {
            if (item is WidgetTile tile && string.Equals(tile.Value as string, categoryId, StringComparison.OrdinalIgnoreCase))
            {
                tile.BringIntoView();
                break;
            }
        }
    }

    private void AnimateCategoryFade()
    {
        if (_isRebuildingCategories) return;
        if (StationItemsControl == null) return;

        // Reset scroll position to top on category change
        StationScrollViewer?.ScrollToVerticalOffset(0);

        // Ultra-snappy micro-fade (100ms): starts from 0.35 opacity so there is no black flash or flicker
        var fadeAnimation = new DoubleAnimation
        {
            From = 0.35,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = FadeEasing
        };

        StationItemsControl.BeginAnimation(UIElement.OpacityProperty, fadeAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnTileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Suppress right-click context menu entirely on the '+' custom station placeholder tile
        if (sender is FrameworkElement { DataContext: RadioStationItemViewModel { IsAddPlaceholder: true } })
        {
            e.Handled = true;
        }
    }
}
