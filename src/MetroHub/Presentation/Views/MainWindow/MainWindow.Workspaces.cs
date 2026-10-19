using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
using MetroHub.Presentation.Controls;
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
            _activeStrip.RemoveAll(item => item.Canvas == canvas);
            CanvasHostPanel?.Children.Remove(canvas);
            canvas.DataContext = null;
        }
    }

    private sealed class ActiveStripItem
    {
        public WorkspaceCanvasControl Canvas { get; }
        public TranslateTransform Transform { get; }
        public double SlotX { get; set; }

        public ActiveStripItem(WorkspaceCanvasControl canvas, TranslateTransform transform, double slotX)
        {
            Canvas = canvas;
            Transform = transform;
            SlotX = slotX;
        }
    }

    private readonly List<ActiveStripItem> _activeStrip = new(capacity: 6);
    private double _cameraX;
    private double _cameraVelocity;
    private double _targetCameraX;
    private double _baseScrollOffset;
    private bool _isTransitionAnimating;
    private long _lastTransitionTimestamp;
    private TimeSpan _lastTransitionRenderTime = TimeSpan.MinValue;
    private WorkspaceCanvasControl? _transitionTargetCanvas;
    private WorkspaceModel? _transitionTargetModel;
    private WorkspaceModel? _lastOutgoingWorkspace;

    private const double TransitionSpringOmega = 22.0;
    private const double TransitionRestPosThreshold = 0.5;
    private const double TransitionRestVelThreshold = 5.0;

    private static double CalculateWorkspaceMaxBottom(WorkspaceModel ws)
    {
        double maxTileBottom = ws.Tiles.Count > 0 ? ws.Tiles.Max(t => t.Y + t.HeightPixels) + 120 : 600;
        double maxGroupBottom = ws.Groups.Count > 0 ? ws.Groups.Max(g => g.Y + 120) : 600;
        return Math.Max(maxTileBottom, maxGroupBottom);
    }

    private void InitializeWorkspaces()
    {
        var wm = WorkspaceManager.Instance;
        wm.WorkspaceChanging += OnWorkspaceChanging;
        wm.WorkspaceChanged += OnWorkspaceChanged;
        wm.WorkspacePrewarmRequested += OnWorkspacePrewarmRequested;
        wm.RequestRename += OnWorkspaceRequestRename;
        wm.RequestDelete += OnWorkspaceRequestDelete;
        wm.TileMoved += OnWorkspaceTileMoved;
        PreviewKeyDown += OnWorkspacesPreviewKeyDown;
    }

    private void OnWorkspacePrewarmRequested(string workspaceId)
    {
        if (string.Equals(_activeCanvas?.Workspace?.Id, workspaceId, StringComparison.OrdinalIgnoreCase)) return;
        var ws = WorkspaceManager.Instance.Workspaces.FirstOrDefault(w => string.Equals(w.Id, workspaceId, StringComparison.OrdinalIgnoreCase));
        if (ws == null) return;
        var canvas = GetOrCreateCanvas(ws);
        PrewarmCanvasCache(canvas);
    }

    private void PrewarmCanvasCache(WorkspaceCanvasControl canvas)
    {
        if (canvas.CacheMode != null) return;
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

        canvas.CacheMode = new BitmapCache
        {
            RenderAtScale = dpiScale,
            EnableClearType = true,
            SnapsToDevicePixels = true
        };
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
            symbol: Wpf.Ui.Controls.SymbolRegular.Delete24,
            showCancelButton: false);

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
            if (ContentScrollViewer != null)
            {
                outgoing.ScrollOffset = ContentScrollViewer.VerticalOffset;
            }
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

        if (_activeCanvas?.Workspace is { } outgoingWs && ContentScrollViewer != null)
        {
            outgoingWs.ScrollOffset = ContentScrollViewer.VerticalOffset;
        }

        // If window is not yet loaded or not visible, perform instant swap
        if (!IsLoaded || !IsVisible || CanvasHostPanel == null)
        {
            ApplyWorkspaceData(target);
            UpdateCanvasHeight();
            if (ContentScrollViewer != null)
            {
                SmoothPhysicsScrollBehavior.ResetOffset(ContentScrollViewer, target.ScrollOffset);
            }
            return;
        }

        var incomingCanvas = GetOrCreateCanvas(target);

        // If same canvas as active and not animating, nothing to transition
        if (!_isTransitionAnimating && (_activeCanvas == null || _activeCanvas == incomingCanvas))
        {
            ApplyWorkspaceData(target);
            return;
        }

        // If already heading to this exact workspace during animation, no-op
        if (_isTransitionAnimating && _transitionTargetCanvas == incomingCanvas)
        {
            return;
        }

        double hostWidth = CanvasHostPanel.ActualWidth;
        double slideDistance = hostWidth > 100.0 ? hostWidth : Math.Max(800.0, ActualWidth);

        var wm = WorkspaceManager.Instance;
        PrewarmCanvasCache(incomingCanvas);

        if (!_isTransitionAnimating)
        {
            var outgoingCanvas = _activeCanvas ?? incomingCanvas;
            PrewarmCanvasCache(outgoingCanvas);

            int outgoingIndex = _lastOutgoingWorkspace != null ? wm.Workspaces.IndexOf(_lastOutgoingWorkspace) : -1;
            int incomingIndex = wm.Workspaces.IndexOf(target);
            double direction = incomingIndex >= outgoingIndex ? 1.0 : -1.0;

            _activeStrip.Clear();
            _cameraX = 0.0;
            _cameraVelocity = 0.0;
            _baseScrollOffset = ContentScrollViewer?.VerticalOffset ?? 0.0;

            var outTransform = new TranslateTransform(0, 0);
            outgoingCanvas.RenderTransform = outTransform;
            outgoingCanvas.Opacity = 1.0;
            outgoingCanvas.Visibility = Visibility.Visible;
            Panel.SetZIndex(outgoingCanvas, 0);
            _activeStrip.Add(new ActiveStripItem(outgoingCanvas, outTransform, 0.0));

            double enterSlotX = direction * slideDistance;
            double enterOffsetY = _baseScrollOffset - target.ScrollOffset;
            var inTransform = new TranslateTransform(enterSlotX, enterOffsetY);
            incomingCanvas.RenderTransform = inTransform;
            incomingCanvas.Opacity = 1.0;
            incomingCanvas.Visibility = Visibility.Visible;
            Panel.SetZIndex(incomingCanvas, 1);
            _activeStrip.Add(new ActiveStripItem(incomingCanvas, inTransform, enterSlotX));

            _targetCameraX = enterSlotX;
            _transitionTargetCanvas = incomingCanvas;
            _transitionTargetModel = target;

            StartTransitionLoop();

            ApplyWorkspaceData(target, skipCanvasVisibility: true);
        }
        else
        {
            // Rapid multi-click interruption: continuous momentum preservation on virtual strip
            int currentTargetIndex = _transitionTargetModel != null ? wm.Workspaces.IndexOf(_transitionTargetModel) : -1;
            int newTargetIndex = wm.Workspaces.IndexOf(target);
            double direction = newTargetIndex >= currentTargetIndex ? 1.0 : -1.0;

            var existingItem = _activeStrip.FirstOrDefault(item => item.Canvas == incomingCanvas);
            if (existingItem != null)
            {
                _targetCameraX = existingItem.SlotX;
            }
            else
            {
                double nextSlotX = _targetCameraX + (direction * slideDistance);
                double enterOffsetY = _baseScrollOffset - target.ScrollOffset;
                var inTransform = new TranslateTransform(nextSlotX - _cameraX, enterOffsetY);
                incomingCanvas.RenderTransform = inTransform;
                incomingCanvas.Opacity = 1.0;
                incomingCanvas.Visibility = Visibility.Visible;
                Panel.SetZIndex(incomingCanvas, _activeStrip.Count + 1);
                _activeStrip.Add(new ActiveStripItem(incomingCanvas, inTransform, nextSlotX));
                _targetCameraX = nextSlotX;
            }

            _transitionTargetCanvas = incomingCanvas;
            _transitionTargetModel = target;

            ApplyWorkspaceData(target, skipCanvasVisibility: true);
        }
    }

    private void StartTransitionLoop()
    {
        if (_isTransitionAnimating) return;
        _isTransitionAnimating = true;
        _lastTransitionTimestamp = Stopwatch.GetTimestamp();
        _lastTransitionRenderTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnTransitionRendering;
    }

    private void OnTransitionRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs renderingArgs)
        {
            if (renderingArgs.RenderingTime == _lastTransitionRenderTime) return;
            _lastTransitionRenderTime = renderingArgs.RenderingTime;
        }

        long now = Stopwatch.GetTimestamp();
        double dt = (now - _lastTransitionTimestamp) / (double)Stopwatch.Frequency;
        _lastTransitionTimestamp = now;

        if (dt <= 0.0001 || dt > 0.05)
        {
            dt = 1.0 / 120.0;
        }

        double displacement = _cameraX - _targetCameraX;
        double exp = Math.Exp(-TransitionSpringOmega * dt);
        double temp = (_cameraVelocity + TransitionSpringOmega * displacement) * dt;

        _cameraX = _targetCameraX + (displacement + temp) * exp;
        _cameraVelocity = (_cameraVelocity - TransitionSpringOmega * temp) * exp;

        bool isAtRest = Math.Abs(_cameraX - _targetCameraX) < TransitionRestPosThreshold
                     && Math.Abs(_cameraVelocity) < TransitionRestVelThreshold;

        if (isAtRest)
        {
            _cameraX = _targetCameraX;
            _cameraVelocity = 0.0;
        }

        for (int i = 0; i < _activeStrip.Count; i++)
        {
            var item = _activeStrip[i];
            item.Transform.X = item.SlotX - _cameraX;
        }

        if (isAtRest)
        {
            CompleteTransition();
        }
    }

    private void CompleteTransition()
    {
        if (!_isTransitionAnimating) return;
        _isTransitionAnimating = false;
        CompositionTarget.Rendering -= OnTransitionRendering;

        var winnerCanvas = _transitionTargetCanvas;
        var winnerModel = _transitionTargetModel;

        for (int i = 0; i < _activeStrip.Count; i++)
        {
            var item = _activeStrip[i];
            if (item.Canvas != winnerCanvas)
            {
                item.Canvas.Visibility = Visibility.Collapsed;
                item.Canvas.CacheMode = null;
                item.Canvas.RenderTransform = null;
            }
        }

        _activeStrip.Clear();

        if (winnerCanvas != null && winnerModel != null)
        {
            winnerCanvas.RenderTransform = null;
            winnerCanvas.Visibility = Visibility.Visible;
            Panel.SetZIndex(winnerCanvas, 0);
            _activeCanvas = winnerCanvas;

            double targetMaxBottom = CalculateWorkspaceMaxBottom(winnerModel);
            if (MainCanvasGrid != null)
            {
                MainCanvasGrid.MinHeight = Math.Max(MainCanvasGrid.MinHeight, targetMaxBottom);
            }

            if (ContentScrollViewer != null)
            {
                SmoothPhysicsScrollBehavior.ResetOffset(ContentScrollViewer, winnerModel.ScrollOffset);
            }

            UpdateCanvasHeight();

            // Restore ClearType on stationary active canvas
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                if (!_isTransitionAnimating && _activeCanvas == winnerCanvas)
                {
                    winnerCanvas.CacheMode = null;
                    InvokeOnAwakened(winnerModel.Tiles);
                }
            });
        }

        _transitionTargetCanvas = null;
        _transitionTargetModel = null;
    }

    internal void CancelWorkspaceTransition()
    {
        if (!_isTransitionAnimating) return;
        _isTransitionAnimating = false;
        CompositionTarget.Rendering -= OnTransitionRendering;

        var fallback = _transitionTargetCanvas ?? _activeCanvas;
        var fallbackModel = _transitionTargetModel ?? fallback?.Workspace;
        if (fallback != null)
        {
            fallback.RenderTransform = null;
            fallback.CacheMode = null;
            fallback.Visibility = Visibility.Visible;
            _activeCanvas = fallback;
        }

        for (int i = 0; i < _activeStrip.Count; i++)
        {
            var item = _activeStrip[i];
            if (item.Canvas != fallback)
            {
                item.Canvas.Visibility = Visibility.Collapsed;
                item.Canvas.CacheMode = null;
                item.Canvas.RenderTransform = null;
            }
        }

        _activeStrip.Clear();
        _transitionTargetCanvas = null;
        _transitionTargetModel = null;

        if (fallbackModel != null)
        {
            double targetMaxBottom = CalculateWorkspaceMaxBottom(fallbackModel);
            if (MainCanvasGrid != null)
            {
                MainCanvasGrid.MinHeight = Math.Max(MainCanvasGrid.MinHeight, targetMaxBottom);
            }
            if (ContentScrollViewer != null)
            {
                SmoothPhysicsScrollBehavior.ResetOffset(ContentScrollViewer, fallbackModel.ScrollOffset);
            }
            UpdateCanvasHeight();
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

        if (target.IsLayoutDirty)
        {
            DiscoverGroupsFromTiles();
            EnsureGroupIndices();
            MigrateGroupColumnOffsets();
            UpdateLayoutMetrics();
            UpdateGroupHeaderPositions();
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
            target.IsLayoutDirty = false;
        }
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
