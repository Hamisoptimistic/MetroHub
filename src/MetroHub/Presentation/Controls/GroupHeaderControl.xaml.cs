using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using MetroHub.Core.Models;

namespace MetroHub.Presentation.Controls;

public partial class GroupHeaderControl : UserControl
{
    private Point _dragStartPoint;
    private bool _isPotentialDrag = false;

    public GroupHeaderControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileGroupModel group)
        {
            // Restore icon states from persisted model
            UpdateLockIcon(group.IsLocked);

            if (group.IsEditing)
            {
                BeginEdit();
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    // Hover: fade action buttons in/out
    // ─────────────────────────────────────────────────────────

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        AnimateDragHint(0.7);
        AnimateActionButtons(1.0);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        AnimateDragHint(0.0);

        // Keep buttons visible when group is locked (always-visible indicator)
        bool keepVisible = DataContext is TileGroupModel g && g.IsLocked;
        AnimateActionButtons(keepVisible ? 0.85 : 0.0);
    }

    private void AnimateDragHint(double to)
    {
        if (DragHintIcon == null) return;
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120));
        DragHintIcon.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void AnimateActionButtons(double to)
    {
        if (ActionButtonsPanel == null) return;
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140));
        ActionButtonsPanel.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    // ─────────────────────────────────────────────────────────
    // Title double-click → rename
    // ─────────────────────────────────────────────────────────

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BeginEdit();
            e.Handled = true;
        }
    }

    public void BeginEdit()
    {
        if (DataContext is not TileGroupModel group) return;

        group.IsEditing = true;
        ViewPanel.Visibility = Visibility.Collapsed;
        EditTextBox.Visibility = Visibility.Visible;
        EditTextBox.Text = group.Title;
        EditTextBox.Focus();
        EditTextBox.SelectAll();
    }

    private void CommitEdit()
    {
        if (DataContext is not TileGroupModel group) return;

        string newTitle = EditTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newTitle))
        {
            newTitle = "Group";
        }

        group.Title = newTitle;
        group.IsEditing = false;
        EditTextBox.Visibility = Visibility.Collapsed;
        ViewPanel.Visibility = Visibility.Visible;

        MainWindow.Current?.SaveGroupsAndLayout();
    }

    private void CancelEdit()
    {
        if (DataContext is not TileGroupModel group) return;

        group.IsEditing = false;
        EditTextBox.Visibility = Visibility.Collapsed;
        ViewPanel.Visibility = Visibility.Visible;
    }

    private void OnEditKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelEdit();
            e.Handled = true;
        }
    }

    private void OnEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (EditTextBox.Visibility == Visibility.Visible)
        {
            CommitEdit();
        }
    }

    // ─────────────────────────────────────────────────────────
    // Drag initiation from header
    // ─────────────────────────────────────────────────────────

    private void OnHeaderPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (EditTextBox.Visibility == Visibility.Visible) return;

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _dragStartPoint = e.GetPosition(this);
            _isPotentialDrag = true;
        }
    }

    private void OnHeaderPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPotentialDrag || EditTextBox.Visibility == Visibility.Visible) return;

        if (e.LeftButton == MouseButtonState.Pressed && DataContext is TileGroupModel group)
        {
            Point current = e.GetPosition(this);
            Vector diff = current - _dragStartPoint;

            if (Math.Abs(diff.X) > 6 || Math.Abs(diff.Y) > 6)
            {
                _isPotentialDrag = false;
                MainWindow.Current?.StartGroupDrag(group, e);
            }
        }
    }

    private void OnHeaderPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPotentialDrag = false;
    }

    // ─────────────────────────────────────────────────────────
    // Action buttons — stop header drag from firing
    // ─────────────────────────────────────────────────────────

    private void OnActionButtonMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only stop header drag — do NOT set e.Handled=true (that kills the Button's Click event)
        _isPotentialDrag = false;
    }

    // ─────────────────────────────────────────────────────────
    // Lock button
    // ─────────────────────────────────────────────────────────

    private void OnLockClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TileGroupModel group) return;

        group.IsLocked = !group.IsLocked;
        UpdateLockIcon(group.IsLocked);

        // If now locked, keep buttons visible at reduced opacity as a persistent indicator
        AnimateActionButtons(group.IsLocked ? 0.85 : 0.0);

        MainWindow.Current?.SaveGroupsAndLayout();
        e.Handled = true;
    }

    private void UpdateLockIcon(bool isLocked)
    {
        if (LockIcon == null) return;
        LockIcon.Symbol = isLocked
            ? Wpf.Ui.Controls.SymbolRegular.LockClosed24
            : Wpf.Ui.Controls.SymbolRegular.LockOpen24;
        LockIcon.Foreground = isLocked
            ? new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#60CDFF"))
            : new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A0FFFFFF"));

        LockButton.ToolTip = isLocked ? "Unlock group" : "Lock group";
    }

    // ─────────────────────────────────────────────────────────
    // Context menu handlers
    // ─────────────────────────────────────────────────────────

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        BeginEdit();
    }

    private void OnColorSelectClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string hex && DataContext is TileGroupModel group)
        {
            MainWindow.Current?.SetGroupColor(group, hex);
        }
    }

    private void OnUngroupClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileGroupModel group)
        {
            MainWindow.Current?.UngroupTiles(group);
        }
    }

    private void OnDeleteGroupClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileGroupModel group)
        {
            MainWindow.Current?.DeleteGroupAndTiles(group);
        }
    }
}
