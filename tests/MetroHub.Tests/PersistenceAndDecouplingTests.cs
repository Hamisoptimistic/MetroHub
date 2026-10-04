using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.Clock;
using MetroHub.Widgets.Catalog.Habit;
using MetroHub.Widgets.Catalog.Photos;
using MetroHub.Widgets.Catalog.Quotes;
using MetroHub.Widgets.Catalog.Rover;
using MetroHub.Widgets.Catalog.Template;
using MetroHub.Widgets.Messaging;
using Xunit;

namespace MetroHub.Tests;

[Collection("StorageTests")]
public class PersistenceAndDecouplingTests : IDisposable
{
    private readonly string _sandboxDir;

    public PersistenceAndDecouplingTests()
    {
        StorageService.ResetPending();
        _sandboxDir = Path.Combine(Path.GetTempPath(), "MetroHub_TestSandbox_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandboxDir);
        AppPaths.CustomAppDataDir = _sandboxDir;
    }

    public void Dispose()
    {
        try
        {
            StorageService.Flush();
            StorageService.ResetPending();
        }
        catch { }
        finally
        {
            AppPaths.CustomAppDataDir = null;
            try
            {
                if (Directory.Exists(_sandboxDir))
                {
                    Directory.Delete(_sandboxDir, recursive: true);
                }
            }
            catch { }
        }
    }

    [Fact]
    public void StorageService_Flush_SynchronouslyWritesPendingLayout()
    {
        var testTiles = new ObservableCollection<TileModel>
        {
            new()
            {
                Id = "test_persistence_tile_1",
                Title = "Flush Test Tile",
                TileType = TileType.App,
                TargetPath = "C:\\Windows\\notepad.exe",
                Col = 0,
                Row = 1,
                X = 0,
                Y = 48
            }
        };

        StorageService.SaveLayout(testTiles);
        StorageService.Flush();

        var loaded = StorageService.LoadLayout();
        Assert.NotNull(loaded);
        Assert.Contains(loaded, t => t.Id == "test_persistence_tile_1");
        StorageService.Flush();
    }

    [Fact]
    public void StorageService_Flush_SynchronouslyWritesPendingGroups()
    {
        var testGroups = new ObservableCollection<TileGroupModel>
        {
            new()
            {
                Id = "test_group_flush_1",
                Title = "Flush Group",
                Col = 0,
                Row = 0,
                X = 10,
                Y = 74
            }
        };

        StorageService.SaveGroups(testGroups);
        StorageService.Flush();

        var loaded = StorageService.LoadGroups();
        Assert.NotNull(loaded);
        Assert.Contains(loaded, g => g.Id == "test_group_flush_1");
        StorageService.Flush();
    }

    [Fact]
    public void StorageService_Flush_SynchronouslyWritesPendingSettings()
    {
        var settings = StorageService.LoadSettings();
        settings.GridBaseSize = 72;

        StorageService.SaveSettings(settings);
        StorageService.Flush();

        var loaded = StorageService.LoadSettings();
        Assert.NotNull(loaded);
        Assert.Equal(72, loaded.GridBaseSize);
        StorageService.Flush();
    }

    [Fact]
    public void ClockWidget_SaveSettings_DispatchesWidgetSettingsChangedMessage()
    {
        WpfTestHost.RunSta(() =>
        {
            var model = new TileModel
            {
                Id = "clock_test_tile",
                TileType = TileType.Widget,
                TargetPath = "clock"
            };

            WidgetSettingsChangedMessage? receivedMessage = null;
            object recipient = new();

            WeakReferenceMessenger.Default.Register<object, WidgetSettingsChangedMessage>(
                recipient,
                (r, msg) =>
                {
                    if (msg.TileId == model.Id)
                    {
                        receivedMessage = msg;
                    }
                });

            try
            {
                using var vm = new ClockWidgetViewModel(model);
                vm.SetTimeFormat(false);

                Assert.NotNull(receivedMessage);
                Assert.Equal(model.Id, receivedMessage.TileId);
                Assert.NotNull(receivedMessage.SettingsJson);
                Assert.Contains("is24HourFormat", receivedMessage.SettingsJson, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                WeakReferenceMessenger.Default.Unregister<WidgetSettingsChangedMessage>(recipient);
            }
        });
    }

    [Fact]
    public void Widgets_SaveSettings_HeadlessExecutionDoesNotThrowNullReference()
    {
        WpfTestHost.RunSta(() =>
        {
            // Verify widgets execute SaveSettings in a headless environment without MainWindow.Current
            Assert.Null(MainWindow.Current);

            // 1. Clock
            var clockModel = new TileModel { Id = "test_clock", TargetPath = "clock" };
            using (var clock = new ClockWidgetViewModel(clockModel))
            {
                clock.SaveSettings();
                Assert.NotNull(clockModel.SettingsJson);
            }

            // 2. Template
            var templateModel = new TileModel { Id = "test_template", TargetPath = "template" };
            using (var template = new TemplateWidgetViewModel(templateModel))
            {
                template.SaveSettings();
                Assert.NotNull(templateModel.SettingsJson);
            }

            // 3. Quotes
            var quotesModel = new TileModel { Id = "test_quotes", TargetPath = "quotes" };
            using (var quotes = new QuotesWidgetViewModel(quotesModel))
            {
                quotes.SaveSettings();
                Assert.NotNull(quotesModel.SettingsJson);
            }

            // 4. Photos
            var photosModel = new TileModel { Id = "test_photos", TargetPath = "photos" };
            using (var photos = new PhotosWidgetViewModel(photosModel))
            {
                photos.SaveSettings();
                Assert.NotNull(photosModel.SettingsJson);
            }

            // 5. Habit
            var habitModel = new TileModel { Id = "test_habit", TargetPath = "habit" };
            using (var habit = new HabitWidgetViewModel(habitModel))
            {
                habit.SaveSettings();
                Assert.NotNull(habitModel.SettingsJson);
            }

            // 8. Rover
            var roverModel = new TileModel { Id = "test_rover", TargetPath = "rover" };
            using (var rover = new RoverWidgetViewModel(roverModel))
            {
                rover.SaveSettings();
                Assert.NotNull(roverModel.SettingsJson);
            }
        });
    }
}
