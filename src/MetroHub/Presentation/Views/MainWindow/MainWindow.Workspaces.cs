using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

        // If window is not yet loaded or not visible, perform instant swap with zero animation
        if (!IsLoaded || !IsVisible || MainCanvasGrid == null)
        {
            ApplyWorkspaceData(target);
            return;
        }

        // Cancel any in-flight transition and complete it immediately
        if (_isWorkspaceTransitionRunning)
        {
            MainCanvasGrid.BeginAnimation(UIElement.OpacityProperty, null);
            _isWorkspaceTransitionRunning = false;
        }

        _isWorkspaceTransitionRunning = true;

        var fadeOutEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeInEase = new CubicEase { EasingMode = EasingMode.EaseIn };

        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = fadeOutEase
        };

        fadeOut.Completed += (s, e) =>
        {
            try
            {
                // In-memory swap while canvas opacity is 0.0
                ApplyWorkspaceData(target);

                var fadeIn = new DoubleAnimation
                {
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = fadeInEase
                };

                fadeIn.Completed += (s2, e2) =>
                {
                    _isWorkspaceTransitionRunning = false;
                    MainCanvasGrid.BeginAnimation(UIElement.OpacityProperty, null);
                    MainCanvasGrid.Opacity = 1.0;

                    // Deferred wake-up at DispatcherPriority.Loaded
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                    {
                        InvokeOnAwakened(target.Tiles);
                    });
                };

                MainCanvasGrid.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            }
            catch (Exception ex)
            {
                Safe.Log("MainWindow.TransitionToWorkspace", ex);
                _isWorkspaceTransitionRunning = false;
                MainCanvasGrid.BeginAnimation(UIElement.OpacityProperty, null);
                MainCanvasGrid.Opacity = 1.0;
            }
        };

        MainCanvasGrid.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void ApplyWorkspaceData(WorkspaceModel target)
    {
        if (_activeCanvas != null && _activeCanvas.Workspace?.Id != target.Id)
        {
            _activeCanvas.Visibility = Visibility.Collapsed;
        }

        var targetCanvas = GetOrCreateCanvas(target);
        targetCanvas.Visibility = Visibility.Visible;
        _activeCanvas = targetCanvas;

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
