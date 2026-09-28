using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MetroHub.Widgets.Catalog.Markdown;

/// <summary>
/// Interaction logic for MarkdownWidgetView.xaml.
/// Forwards lifecycle events so the VM can defer the first render past startup and skip rendering
/// while the tile is off-screen. Keyboard shortcuts are declared as <c>InputBindings</c> on the
/// control (they route from the focused descendant), so no widget ever needs Focusable="True" and
/// nothing steals keyboard focus from the hub.
/// </summary>
public partial class MarkdownWidgetView : UserControl
{
    private MarkdownWidgetViewModel? _vm;

    public MarkdownWidgetView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => Rebind();
    }

    private void Rebind()
    {
        Detach();
        if (!IsLoaded || DataContext is not MarkdownWidgetViewModel vm)
        {
            return;
        }

        _vm = vm;
        vm.ActiveDocumentReplaced += OnActiveDocumentReplaced;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        InstallShortcuts(vm);
    }

    private void Detach()
    {
        if (_vm == null)
        {
            return;
        }

        _vm.ActiveDocumentReplaced -= OnActiveDocumentReplaced;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = null;
        InputBindings.Clear();
    }

    /// <summary>
    /// Widget-scoped productivity shortcuts. Declared here rather than on the window so they only
    /// fire while focus is inside this widget, and as InputBindings rather than a PreviewKeyDown
    /// handler so no focusable wrapper (and no focus stealing) is required.
    /// </summary>
    private void InstallShortcuts(MarkdownWidgetViewModel vm)
    {
        InputBindings.Clear();
        InputBindings.Add(new KeyBinding(vm.SaveAsCommand, Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(vm.SaveCommand, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(vm.OpenMarkdownFileCommand, Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(vm.NewTabCommand, Key.T, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(vm.CloseCurrentTabCommand, Key.W, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(vm.ToggleModeCommand, Key.E, ModifierKeys.Control));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Ctrl+E into Write mode means "I want to type": put the caret back in the editor instead
        // of leaving focus on a now-hidden preview (or on whatever had it before).
        if (e.PropertyName == nameof(MarkdownWidgetViewModel.ActiveTab) && _vm?.IsWriteTab == true)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_vm?.IsWriteTab == true)
                {
                    MarkdownEditor.Focus();
                }
            }, DispatcherPriority.Input);
        }
    }

    /// <summary>
    /// The VM replaced the text under the single shared editor (tab switch, file open into the
    /// current tab). WPF does not clear a TextBox's undo stack when Text changes programmatically,
    /// so without this Ctrl+Z would paste the previous document into the new tab — and the next
    /// autosave would write it to that tab's file.
    /// </summary>
    private void OnActiveDocumentReplaced(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ResetEditorUndoHistory();
            MarkdownEditor.CaretIndex = 0;
            MarkdownEditor.SelectionLength = 0;
            MarkdownEditor.ScrollToHome();
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// There is no public ClearUndo() on TextBoxBase: toggling IsUndoEnabled (and re-applying
    /// UndoLimit) is the supported way to drop the accumulated stack without touching the text.
    /// </summary>
    private void ResetEditorUndoHistory()
    {
        bool undoEnabled = MarkdownEditor.IsUndoEnabled;
        MarkdownEditor.IsUndoEnabled = false;
        MarkdownEditor.IsUndoEnabled = undoEnabled;

        int limit = MarkdownEditor.UndoLimit;
        MarkdownEditor.UndoLimit = 0;
        MarkdownEditor.UndoLimit = limit;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Rebind();

        if (DataContext is MarkdownWidgetViewModel vm)
        {
            if (vm.DocumentStyle == null && Resources["MarkdownDocumentStyle"] is Style docStyle)
            {
                vm.DocumentStyle = docStyle;
            }
            vm.OnVisibilityChanged(IsVisible);
            vm.OnViewLoaded();
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is MarkdownWidgetViewModel vm)
        {
            vm.OnVisibilityChanged(IsVisible);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MarkdownWidgetViewModel vm)
        {
            vm.OnVisibilityChanged(false);
        }
        Detach();
    }

    private void MoreMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.DataContext = DataContext;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }
}
