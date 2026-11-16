using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controls;
using Xunit;

namespace MetroHub.Tests;

public class RunningAppTrackerTests
{
    [Fact]
    public void TileControl_WhenModelAlreadyRunning_ShowsRunningIndicator()
    {
        WpfTestHost.RunSta(() =>
        {
            var model = new TileModel { Id = "test1", Title = "Test Tile", IsRunning = true };
            var tile = new TileControl { DataContext = model };
            var window = new Window { Content = tile, Width = 300, Height = 300 };
            window.Show();
            
            // Allow storyboard animation (180ms) to complete
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);

            Assert.Equal(1.0, tile.RunningIndicator.Opacity, precision: 1);

            window.Close();
        });
    }

    [Fact]
    public void TileControl_WhenModelNotRunning_HidesRunningIndicator()
    {
        WpfTestHost.RunSta(() =>
        {
            var model = new TileModel { Id = "test2", Title = "Test Tile 2", IsRunning = false };
            var tile = new TileControl { DataContext = model };
            var window = new Window { Content = tile, Width = 300, Height = 300 };
            window.Show();

            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            Assert.Equal(0.0, tile.RunningIndicator.Opacity);

            window.Close();
        });
    }

    [Fact]
    public void TileModel_IsRunning_RaisesPropertyChangedNotification()
    {
        var tile = new TileModel { Title = "Test App", TargetPath = "notepad.exe" };
        var changedProperties = new List<string?>();
        tile.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName);

        tile.IsRunning = true;
        Assert.True(tile.IsRunning);
        Assert.Contains(nameof(TileModel.IsRunning), changedProperties);

        changedProperties.Clear();
        // Setting the identical value should not trigger another notification
        tile.IsRunning = true;
        Assert.Empty(changedProperties);

        tile.IsRunning = false;
        Assert.False(tile.IsRunning);
        Assert.Contains(nameof(TileModel.IsRunning), changedProperties);
    }

    [Fact]
    public void TileModel_IsRunning_IsExcludedFromSerialization()
    {
        var tile = new TileModel
        {
            Title = "Calculated App",
            TargetPath = "calc.exe",
            IsRunning = true
        };

        string json = JsonSerializer.Serialize(tile);
        Assert.DoesNotContain("\"IsRunning\"", json);
    }

    [Fact]
    public void RunningAppTrackerService_Dispose_IsIdempotent()
    {
        using var tracker = new RunningAppTrackerService();
        tracker.Dispose();
        tracker.Dispose(); // Second call must not throw ObjectDisposedException
    }

    [Fact]
    public void RunningAppTrackerService_UpdateTiles_ResetsNonAppTilesRunningState()
    {
        using var tracker = new RunningAppTrackerService();
        var urlTile = new TileModel
        {
            Title = "Documentation",
            TargetPath = "https://example.com",
            TileType = TileType.WebUrl,
            IsRunning = true
        };

        tracker.UpdateTiles(new[] { urlTile });
        Assert.False(urlTile.IsRunning);
    }

    [Fact]
    public void RunningAppTrackerService_UpdateTiles_HandlesNullAndEmptySafely()
    {
        using var tracker = new RunningAppTrackerService();
        tracker.UpdateTiles(null);
        tracker.UpdateTiles(Array.Empty<TileModel>());
    }

    [Fact]
    public void RunningAppTrackerService_WhenDisabledInSettings_DoesNotInstallHooksAndResetsRunningState()
    {
        bool originalSetting = SettingsService.Instance.Current.ShowRunningAppIndicators;
        try
        {
            SettingsService.Instance.Current.ShowRunningAppIndicators = false;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));

            using var tracker = new RunningAppTrackerService();
            var appTile = new TileModel
            {
                Title = "Calculator",
                TargetPath = "calc.exe",
                TileType = TileType.App,
                IsRunning = true
            };

            tracker.Initialize(new[] { appTile });

            Assert.False(tracker.AreHooksInstalled);
            Assert.False(appTile.IsRunning);
        }
        finally
        {
            SettingsService.Instance.Current.ShowRunningAppIndicators = originalSetting;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));
        }
    }

    [Fact]
    public void RunningAppTrackerService_SettingToggledOff_ClearsRunningStateAndDropsResources()
    {
        bool originalSetting = SettingsService.Instance.Current.ShowRunningAppIndicators;
        try
        {
            SettingsService.Instance.Current.ShowRunningAppIndicators = true;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));

            using var tracker = new RunningAppTrackerService();
            var appTile = new TileModel
            {
                Title = "Calculator",
                TargetPath = "calc.exe",
                TileType = TileType.App,
                IsRunning = true
            };

            tracker.Initialize(new[] { appTile });
            Assert.True(tracker.AreHooksInstalled);

            // Toggle off
            SettingsService.Instance.Current.ShowRunningAppIndicators = false;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));

            Assert.False(tracker.AreHooksInstalled);
            Assert.False(appTile.IsRunning);
            Assert.Equal(0, tracker.TrackedProcessCount);

            // Toggle back on
            SettingsService.Instance.Current.ShowRunningAppIndicators = true;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));

            Assert.True(tracker.AreHooksInstalled);
        }
        finally
        {
            SettingsService.Instance.Current.ShowRunningAppIndicators = originalSetting;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));
        }
    }

    [Fact]
    public void WorkspacesSettingsViewModel_Toggle_UpdatesCurrentSettingsAndNotifies()
    {
        bool originalSetting = SettingsService.Instance.Current.ShowRunningAppIndicators;
        try
        {
            SettingsService.Instance.Current.ShowRunningAppIndicators = true;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));

            var vm = new MetroHub.Presentation.Views.Settings.ViewModels.WorkspacesSettingsViewModel();
            Assert.True(vm.ShowRunningAppIndicators);

            vm.ShowRunningAppIndicators = false;
            Assert.False(SettingsService.Instance.Current.ShowRunningAppIndicators);

            vm.ShowRunningAppIndicators = true;
            Assert.True(SettingsService.Instance.Current.ShowRunningAppIndicators);
        }
        finally
        {
            SettingsService.Instance.Current.ShowRunningAppIndicators = originalSetting;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));
        }
    }

    [Fact]
    public void WorkspacesPage_InitializesProperly()
    {
        WpfTestHost.RunSta(() =>
        {
            var page = new MetroHub.Presentation.Views.Settings.Pages.WorkspacesPage();
            Assert.NotNull(page);
        });
    }

    [Fact]
    public void RunningAppTrackerService_RapidToggleStress_CleanlyDropsResourcesWithoutExceptions()
    {
        bool originalSetting = SettingsService.Instance.Current.ShowRunningAppIndicators;
        try
        {
            SettingsService.Instance.Current.ShowRunningAppIndicators = true;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));

            using var tracker = new RunningAppTrackerService();
            var appTile = new TileModel
            {
                Title = "TestApp",
                TargetPath = "notepad.exe",
                TileType = TileType.App,
                IsRunning = true
            };

            tracker.Initialize(new[] { appTile });

            // Rapidly flip the toggle 10 times
            for (int i = 0; i < 10; i++)
            {
                bool newState = (i % 2) != 0;
                SettingsService.Instance.Current.ShowRunningAppIndicators = newState;
                SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));
            }

            // Ensure disabled at the end
            SettingsService.Instance.Current.ShowRunningAppIndicators = false;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));

            Assert.False(tracker.AreHooksInstalled);
            Assert.False(appTile.IsRunning);
            Assert.Equal(0, tracker.TrackedProcessCount);
        }
        finally
        {
            SettingsService.Instance.Current.ShowRunningAppIndicators = originalSetting;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));
        }
    }

    [Fact]
    public async Task RunningAppTrackerService_OffThreadSettingChange_DoesNotDeadlockOrThrow()
    {
        bool originalSetting = SettingsService.Instance.Current.ShowRunningAppIndicators;
        try
        {
            SettingsService.Instance.Current.ShowRunningAppIndicators = true;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));

            using var tracker = new RunningAppTrackerService();
            var appTile = new TileModel
            {
                Title = "TestApp",
                TargetPath = "cmd.exe",
                TileType = TileType.App,
                IsRunning = true
            };

            tracker.Initialize(new[] { appTile });

            await Task.Run(() =>
            {
                SettingsService.Instance.Current.ShowRunningAppIndicators = false;
                SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));

                SettingsService.Instance.Current.ShowRunningAppIndicators = true;
                SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));
            });

            Assert.True(tracker.AreHooksInstalled);
        }
        finally
        {
            SettingsService.Instance.Current.ShowRunningAppIndicators = originalSetting;
            SettingsService.Instance.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));
        }
    }

    [Fact]
    public void SettingsDetailLayoutViewModel_WorkspacesCategory_LoadsWorkspacesSettingsViewModel()
    {
        var shell = new MetroHub.Presentation.Views.Settings.SettingsShellViewModel();
        var workspacesCategory = new MetroHub.Presentation.Views.Settings.ViewModels.SettingsCategoryItem
        {
            Id = "Workspaces",
            Title = "Workspaces & Canvas"
        };
        var categories = new System.Collections.ObjectModel.ObservableCollection<MetroHub.Presentation.Views.Settings.ViewModels.SettingsCategoryItem>
        {
            workspacesCategory
        };
        var layout = new MetroHub.Presentation.Views.Settings.ViewModels.SettingsDetailLayoutViewModel(shell, categories, workspacesCategory);

        Assert.NotNull(layout.CurrentPageViewModel);
        Assert.IsType<MetroHub.Presentation.Views.Settings.ViewModels.WorkspacesSettingsViewModel>(layout.CurrentPageViewModel);
    }
}
