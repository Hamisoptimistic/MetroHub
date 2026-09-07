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
        if (DataContext is TileGroupModel group && group.IsEditing)
        {
            BeginEdit();
        }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (DragHintIcon != null)
        {
            var anim = new DoubleAnimation(0.7, TimeSpan.FromMilliseconds(120));
            DragHintIcon.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (DragHintIcon != null)
        {
            var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120));
            DragHintIcon.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

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
