using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Controls;

public partial class TileControl : UserControl
{
    public static readonly RoutedEvent TileActivatedEvent = EventManager.RegisterRoutedEvent(
        nameof(TileActivated), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TileControl));

    public static readonly RoutedEvent TileUnpinnedEvent = EventManager.RegisterRoutedEvent(
        nameof(TileUnpinned), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TileControl));

    public event RoutedEventHandler TileActivated
    {
        add => AddHandler(TileActivatedEvent, value);
        remove => RemoveHandler(TileActivatedEvent, value);
    }

    public event RoutedEventHandler TileUnpinned
    {
        add => AddHandler(TileUnpinnedEvent, value);
        remove => RemoveHandler(TileUnpinnedEvent, value);
    }

    public static readonly List<TileControl> ActiveTiles = new();

    public TileControl()
    {
        InitializeComponent();
        Loaded += (s, e) => { if (!ActiveTiles.Contains(this)) ActiveTiles.Add(this); };
        Unloaded += (s, e) => { ActiveTiles.Remove(this); };

        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        MouseMove += OnMouseMove;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(RootBorder);
        UpdateRevealPositions(pos);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(RootBorder);
        UpdateRevealPositions(pos);
        AnimateRevealFill(1.0, 100);
        RevealEdgeBorder.BeginAnimation(UIElement.OpacityProperty, null);
        RevealEdgeBorder.Opacity = 1.0;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        AnimateRevealFill(0.0, 200);
        var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        RevealEdgeBorder?.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void UpdateRevealPositions(Point pos)
    {
        if (RevealFillBrush != null)
        {
            RevealFillBrush.Center = pos;
            RevealFillBrush.GradientOrigin = pos;
        }
        if (RevealEdgeBrush != null)
        {
            RevealEdgeBrush.Center = pos;
            RevealEdgeBrush.GradientOrigin = pos;
        }
    }

    private void AnimateRevealFill(double targetOpacity, int durationMs)
    {
        var anim = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        RevealFillBorder?.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    public void UpdateAmbientReveal(Point mouseOnTile, double distance)
    {
        if (IsMouseOver)
        {
            RevealEdgeBorder.Opacity = 1.0;
            return;
        }

        if (RevealEdgeBrush != null)
        {
            RevealEdgeBrush.Center = mouseOnTile;
            RevealEdgeBrush.GradientOrigin = mouseOnTile;
        }

        // Quadratic proximity falloff
        double factor = Math.Clamp(1.0 - (distance / 160.0), 0.0, 1.0);
        double targetOpacity = factor * factor;

        RevealEdgeBorder.BeginAnimation(UIElement.OpacityProperty, null);
        RevealEdgeBorder.Opacity = targetOpacity;
    }

    public void ClearAmbientReveal()
    {
        if (!IsMouseOver && RevealEdgeBorder.Opacity > 0)
        {
            RevealEdgeBorder.BeginAnimation(UIElement.OpacityProperty, null);
            RevealEdgeBorder.Opacity = 0.0;
        }
    }

    public void LaunchTile()
    {
        if (DataContext is TileModel tile && !string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            NativeMethods.LaunchTarget(tile.TargetPath, tile.Arguments, tile.RunAsAdmin);
            RaiseEvent(new RoutedEventArgs(TileActivatedEvent, tile));
        }
    }

    private void AnimateScale(double targetScale)
    {
        var anim = new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        TileScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        TileScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void OnResizeSmallClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            tile.SpanX = 1;
            tile.SpanY = 1;
        }
    }

    private void OnResizeMediumClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            tile.SpanX = 2;
            tile.SpanY = 2;
        }
    }

    private void OnResizeWideClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            tile.SpanX = 4;
            tile.SpanY = 2;
        }
    }

    private void OnRunAsAdminClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile && !string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            NativeMethods.LaunchTarget(tile.TargetPath, tile.Arguments, runAsAdmin: true);
            RaiseEvent(new RoutedEventArgs(TileActivatedEvent, tile));
        }
    }

    private void OnOpenFileLocationClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile && !string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            try
            {
                string target = IconExtractorService.ResolveShortcutTarget(tile.TargetPath);
                if (File.Exists(target))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
                }
            }
            catch { }
        }
    }

    private void OnUnpinClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            RaiseEvent(new RoutedEventArgs(TileUnpinnedEvent, tile));
        }
    }
}
