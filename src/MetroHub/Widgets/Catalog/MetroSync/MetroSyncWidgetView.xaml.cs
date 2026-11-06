using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MetroHub.Widgets.Catalog.MetroSync.Models;

namespace MetroHub.Widgets.Catalog.MetroSync;

/// <summary>
/// Interaction logic for MetroSyncWidgetView.xaml
/// Supports chat feed with auto-hiding scrollbar, bottom docked input row (Enter sends, Shift+Enter newline),
/// and bottom-aware auto-scrolling with clean lifecycle teardown.
/// </summary>
public partial class MetroSyncWidgetView : UserControl
{
    private MetroSyncWidgetViewModel? _viewModel;
    private bool _isSetupDialogOpen;

    public MetroSyncWidgetView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdatePlaceholderVisibility();
        HookViewModel(DataContext as MetroSyncWidgetViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnhookViewModel();
        HookViewModel(e.NewValue as MetroSyncWidgetViewModel);
    }

    private void HookViewModel(MetroSyncWidgetViewModel? vm)
    {
        if (vm == null || ReferenceEquals(_viewModel, vm)) return;

        _viewModel = vm;
        ((INotifyCollectionChanged)_viewModel.Items).CollectionChanged += OnItemsCollectionChanged;
        _viewModel.SetupRequested += OnSetupRequested;

        if (!_viewModel.IsPaired)
        {
            Dispatcher.InvokeAsync(OpenSetupDialog, DispatcherPriority.Background);
        }
    }

    private void UnhookViewModel()
    {
        if (_viewModel != null)
        {
            ((INotifyCollectionChanged)_viewModel.Items).CollectionChanged -= OnItemsCollectionChanged;
            _viewModel.SetupRequested -= OnSetupRequested;
            _viewModel = null;
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && FeedScrollViewer != null)
        {
            bool isNearBottom = (FeedScrollViewer.ScrollableHeight - FeedScrollViewer.VerticalOffset) <= 30.0;
            bool hasOutgoing = e.NewItems?.OfType<MetroSyncMessageItem>().Any(i => i.IsOutgoing) == true;

            if (isNearBottom || hasOutgoing)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    FeedScrollViewer?.ScrollToBottom();
                }, DispatcherPriority.Loaded);
            }
        }
    }

    private void OnSetupRequested(object? sender, EventArgs e)
    {
        OpenSetupDialog();
    }

    private void OpenSetupDialog()
    {
        if (_viewModel == null || _viewModel.IsPaired || _isSetupDialogOpen) return;
        _isSetupDialogOpen = true;
        try
        {
            var window = Window.GetWindow(this) ?? Application.Current?.MainWindow;
            MetroSyncSetupDialog.Show(window, _viewModel);
        }
        finally
        {
            _isSetupDialogOpen = false;
        }
    }

    private void OnInputTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                return;
            }

            e.Handled = true;
            if (DataContext is MetroSyncWidgetViewModel vm && vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
            }
        }
    }

    private void OnInputTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholderVisibility();
    }

    private void UpdatePlaceholderVisibility()
    {
        if (InputPlaceholder != null && InputTextBox != null)
        {
            InputPlaceholder.Visibility = string.IsNullOrEmpty(InputTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void OnHeaderMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.DataContext = btn.DataContext ?? DataContext;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }
}