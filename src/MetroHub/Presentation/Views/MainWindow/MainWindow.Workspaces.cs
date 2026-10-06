using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
using MetroHub.Presentation.Controls.Canvas;
using MetroHub.Presentation.Dialogs;

namespace MetroHub;

public partial class MainWindow
{
    private WorkspaceCanvasControl? _activeCanvas;
    private readonly Dictionary<string, WorkspaceCanvasControl> _canvasPool = new(StringComparer.OrdinalIgnoreCase);

    internal ItemsControl? TilesListBox => _activeCanvas?.TilesListBox;
    internal ItemsControl? GroupsListBox => _activeCanvas?.GroupsListBox;
    internal ItemsControl? GroupTintBackplates => _activeCanvas?.GroupTintBackplates;

    private WorkspaceCanvasControl GetOrCreateCanvas(WorkspaceModel ws)
    {
        if (_canvasPool.TryGetValue(ws.Id, out var existing))
        {
            return existing;
        }

        var canvas = new WorkspaceCanvasControl
        {
            DataContext = ws,
            Visibility = Visibility.Collapsed
        };

        _canvasPool[ws.Id] = canvas;
        CanvasHostPanel?.Children.Add(canvas);
        return canvas;
    }

    private void CleanupWorkspaceCanvas(string workspaceId)
    {
        if (_canvasPool.Remove(workspaceId, out var canvas))
        {
            CanvasHostPanel?.Children.Remove(canvas);
            canvas.DataContext = null;
        }
    }

    private bool _isWorkspaceTransitionRunning;
    private WorkspaceModel? _lastOutgoingWorkspace;

    private void InitializeWorkspaces()
    {
        var wm = WorkspaceManager.Instance;
        wm.WorkspaceChanging += OnWorkspaceChanging;
        wm.WorkspaceChanged += OnWorkspaceChanged;
        wm.RequestRename += OnWorkspaceRequestRename;
        wm.RequestDelete += OnWorkspaceRequestDelete;
        wm.TileMoved += OnWorkspaceTileMoved;
        PreviewKeyDown += OnWorkspacesPreviewKeyDown;
    }

    private void OnWorkspaceRequestRename(WorkspaceModel ws)
    {
        if (ws == null) return;
        var result = WorkspaceEditDialog.Show(this, ws.Name, ws.IconSymbol);
        if (result.HasValue)
        {
            var (newName, newIcon) = result.Value;
            if (!string.IsNullOrWhiteSpace(newName) &&
                (!string.Equals(newName, ws.Name, StringComparison.Ordinal) ||
                 !string.Equals(newIcon, ws.IconSymbol, StringComparison.Ordinal)))
            {
                WorkspaceManager.Instance.UpdateWorkspace(ws.Id, newName, newIcon);
            }
        }
    }

    private void OnWorkspaceRequestDelete(WorkspaceModel ws)
    {
        if (ws == null) return;
        bool confirmed = MetroDialog.Confirm(
            owner: this,
            title: "Delete Workspace",
            subtitle: "Confirm workspace removal",
            message: "Are you sure you want to delete this workspace?",
            detail: $"All tiles in \"{ws.Name}\" will be archived to the workspace trash folder. The active canvas will switch to an available workspace.",
            confirmText: "Delete Workspace",
            isDestructive: true,
            symbol: Wpf.Ui.Controls.SymbolRegular.Delete24);

        if (confirmed)
        {
            CleanupWorkspaceCanvas(ws.Id);
            WorkspaceManager.Instance.DeleteWorkspace(ws.Id);
        }
    }

    private void OnWorkspaceTileMoved()
    {
        _historyService.Clear();
        ClearTileSelection();
        UpdateLayoutMetrics();
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
    }

    private void OnWorkspaceChanging(WorkspaceModel? outgoing, WorkspaceModel incoming)
    {
        _lastOutgoingWorkspace = outgoing;
        if (outgoing != null)
        {
            InvokeOnDormant(outgoing.Tiles);
        }
    }

    private void OnWorkspaceChanged(WorkspaceModel incoming)
    {
        TransitionToWorkspace(incoming);
    }

    private void TransitionToWorkspace(WorkspaceModel target)
    {
        if (target == null) return;

        // If window is not yet loaded or not visible, perform instant swap
        if (!IsLoaded || !IsVisible || CanvasHostPanel == null)
        {
            ApplyWorkspaceData(target);
            return;
        }

        var outgoingCanvas = _activeCanvas;
        var incomingCanvas = GetOrCreateCanvas(target);

        // If same canvas or no outgoing canvas, swap immediately
        if (outgoingCanvas == null || outgoingCanvas == incomingCanvas)
        {
            ApplyWorkspaceData(target);
            return;
        }

        // Cancel any in-flight transition and reset clocks immediately
        if (_isWorkspaceTransitionRunning)
        {
            CleanupTransitionState();
        }

        _isWorkspaceTransitionRunning = true;

        // Determine spatial direction based on workspace rail order
        var wm = WorkspaceManager.Instance;
        int outgoingIndex = _lastOutgoingWorkspace != null ? wm.Workspaces.IndexOf(_lastOutgoingWorkspace) : -1;
        int incomingIndex = wm.Workspaces.IndexOf(target);
        bool movingForward = incomingIndex >= outgoingIndex;

        // Full-screen contiguous slide distance (true Windows 11 Virtual Desktop push)
        double hostWidth = CanvasHostPanel.ActualWidth;
        double slideDistance = hostWidth > 100.0 ? hostWidth : Math.Max(800.0, ActualWidth);
        double exitX = movingForward ? -slideDistance : slideDistance;
        double enterStartX = movingForward ? slideDistance : -slideDistance;

        // Layer order: incoming on top
        Panel.SetZIndex(incomingCanvas, 1);
        Panel.SetZIndex(outgoingCanvas, 0);

        var outgoingTransform = new TranslateTransform(0, 0);
        var incomingTransform = new TranslateTransform(enterStartX, 0);

        outgoingCanvas.RenderTransform = outgoingTransform;
        incomingCanvas.RenderTransform = incomingTransform;

        // 100% Solid Opacity - Zero fading, Zero font antialiasing snapping, Zero ghosting
        outgoingCanvas.Opacity = 1.0;
        incomingCanvas.Opacity = 1.0;
        incomingCanvas.Visibility = Visibility.Visible;
        outgoingCanvas.Visibility = Visibility.Visible;

        // High-DPI physical texture caching with sub-pixel ClearType enabled for locked 120 FPS
        double dpiScale = 1.0;
        try
        {
            dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (dpiScale <= 0) dpiScale = 1.0;
        }
        catch
        {
            dpiScale = 1.0;
        }

        outgoingCanvas.CacheMode = new BitmapCache
        {
            RenderAtScale = dpiScale,
            EnableClearType = true,
            SnapsToDevicePixels = true
        };
        incomingCanvas.CacheMode = new BitmapCache
        {
            RenderAtScale = dpiScale,
            EnableClearType = true,
            SnapsToDevicePixels = true
        };

        // Apply target model metadata, placement, and metrics immediately
        ApplyWorkspaceData(target, skipCanvasVisibility: true);

        // Standard Fluent 2 Deceleration Curve (EaseOut) across 280 ms
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(280);

        var outgoingSlide = new DoubleAnimation
        {
            To = exitX,
            Duration = duration,
            EasingFunction = easeOut
        };
        Timeline.SetDesiredFrameRate(outgoingSlide, 120);

        var incomingSlide = new DoubleAnimation
        {
            To = 0.0,
            Duration = duration,
            EasingFunction = easeOut
        };
        Timeline.SetDesiredFrameRate(incomingSlide, 120);

        EventHandler? onCompleted = null;
        onCompleted = (s, e) =>
        {
            incomingSlide.Completed -= onCompleted;
            CompleteTransition(outgoingCanvas, incomingCanvas, target);
        };

        incomingSlide.Completed += onCompleted;

        // Hardware-composite both contiguous canvases at 120 FPS
        outgoingTransform.BeginAnimation(TranslateTransform.XProperty, outgoingSlide);
        incomingTransform.BeginAnimation(TranslateTransform.XProperty, incomingSlide);
    }

    private void CompleteTransition(WorkspaceCanvasControl outgoingCanvas, WorkspaceCanvasControl incomingCanvas, WorkspaceModel target)
    {
        _isWorkspaceTransitionRunning = false;

        // Teardown outgoing canvas: detach animation clocks and collapse
        outgoingCanvas.Visibility = Visibility.Collapsed;
        outgoingCanvas.CacheMode = null;
        if (outgoingCanvas.RenderTransform is TranslateTransform outTt)
        {
            outTt.BeginAnimation(TranslateTransform.XProperty, null);
        }
        outgoingCanvas.RenderTransform = null;

        // Teardown incoming canvas: detach animation clocks and restore clean vector state
        incomingCanvas.CacheMode = null;
        if (incomingCanvas.RenderTransform is TranslateTransform inTt)
        {
            inTt.BeginAnimation(TranslateTransform.XProperty, null);
        }
        incomingCanvas.RenderTransform = null;
        incomingCanvas.Visibility = Visibility.Visible;

        _activeCanvas = incomingCanvas;

        // Deferred wake-up of active widgets
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            InvokeOnAwakened(target.Tiles);
        });
    }

    private void CleanupTransitionState()
    {
        _isWorkspaceTransitionRunning = false;
        if (CanvasHostPanel != null)
        {
            foreach (UIElement child in CanvasHostPanel.Children)
            {
                if (child is WorkspaceCanvasControl canvas)
                {
                    canvas.CacheMode = null;
                    if (canvas.RenderTransform is TranslateTransform tt)
                    {
                        tt.BeginAnimation(TranslateTransform.XProperty, null);
                    }
                    canvas.RenderTransform = null;
                    if (canvas != _activeCanvas)
                    {
                        canvas.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        canvas.Visibility = Visibility.Visible;
                    }
                }
            }
        }
    }

    private void ApplyWorkspaceData(WorkspaceModel target, bool skipCanvasVisibility = false)
    {
        if (!skipCanvasVisibility)
        {
            if (_activeCanvas != null && _activeCanvas.Workspace?.Id != target.Id)
            {
                _activeCanvas.Visibility = Visibility.Collapsed;
            }

            var targetCanvas = GetOrCreateCanvas(target);
            targetCanvas.Visibility = Visibility.Visible;
            _activeCanvas = targetCanvas;
        }

        Tiles = target.Tiles;
        Groups = target.Groups;

        GridPlacementService.SetActiveGroups(Groups);
        _historyService.SwitchWorkspace(target.Id);
        ClearTileSelection();

        DiscoverGroupsFromTiles();
        EnsureGroupIndices();
        MigrateGroupColumnOffsets();
        CompactGroupGaps();
        UpdateLayoutMetrics();
        GridPlacementService.CleanEmptyGroups(Groups, Tiles);
        UpdateGroupHeaderPositions();
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
    }

    private static void InvokeOnDormant(IEnumerable<TileModel> tiles)
    {
        if (tiles == null) return;
        foreach (var tile in tiles)
        {
            try
            {
                var vm = tile.WidgetViewModel ?? tile.TileContent;
                if (vm is IDormancyAware dormantVm)
                {
                    dormantVm.OnDormant();
                }
            }
            catch (Exception ex)
            {
                Safe.Log("MainWindow.InvokeOnDormant", ex);
            }
        }
    }

    private static void InvokeOnAwakened(IEnumerable<TileModel> tiles)
    {
        if (tiles == null) return;
        foreach (var tile in tiles)
        {
            try
            {
                var vm = tile.WidgetViewModel ?? tile.TileContent;
                if (vm is IDormancyAware awakenedVm)
                {
                    awakenedVm.OnAwakened();
                }
            }
            catch (Exception ex)
            {
                Safe.Log("MainWindow.InvokeOnAwakened", ex);
            }
        }
    }

    private void OnWorkspacesPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Bypass if focus is inside any text input control (search, rename, dialog textboxes, password boxes)
        if (Keyboard.FocusedElement is TextBoxBase ||
            Keyboard.FocusedElement is PasswordBox)
        {
            return;
        }

        // Only handle Ctrl + Shift modifiers (AltGr safe: never Ctrl + Alt)
        var modifiers = Keyboard.Modifiers;
        if (modifiers != (ModifierKeys.Control | ModifierKeys.Shift))
        {
            return;
        }

        var wm = WorkspaceManager.Instance;
        var workspaces = wm.Workspaces;
        if (workspaces.Count == 0) return;

        int targetIndex = -1;

        // Number keys 1..9 (top row)
        if (e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            targetIndex = e.Key - Key.D1;
        }
        // Number keys 1..9 (numpad)
        else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9)
        {
            targetIndex = e.Key - Key.NumPad1;
        }
        // Left arrow: previous workspace
        else if (e.Key == Key.Left)
        {
            int currentIndex = wm.ActiveWorkspace != null ? workspaces.IndexOf(wm.ActiveWorkspace) : 0;
            if (currentIndex > 0)
            {
                targetIndex = currentIndex - 1;
            }
            else if (workspaces.Count > 1)
            {
                targetIndex = workspaces.Count - 1; // Wrap to last
            }
        }
        // Right arrow: next workspace
        else if (e.Key == Key.Right)
        {
            int currentIndex = wm.ActiveWorkspace != null ? workspaces.IndexOf(wm.ActiveWorkspace) : 0;
            if (currentIndex >= 0 && currentIndex < workspaces.Count - 1)
            {
                targetIndex = currentIndex + 1;
            }
            else if (workspaces.Count > 1)
            {
                targetIndex = 0; // Wrap to first
            }
        }

        if (targetIndex >= 0 && targetIndex < workspaces.Count)
        {
            var targetWs = workspaces[targetIndex];
            if (targetWs != wm.ActiveWorkspace)
            {
                wm.SwitchWorkspace(targetWs.Id);
                e.Handled = true;
            }
        }
    }
}
