using System;
using System.Collections.Generic;
using System.Linq;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets;
using MetroHub.Widgets.Registry;
using Xunit;

namespace MetroHub.Tests;

public class WidgetLifecycleContractTests
{
    public static IEnumerable<object[]> GetWidgetDefinitionIds()
    {
        // Enumerate every registered widget definition ID
        return WidgetRegistry.GetAll().Select(def => new object[] { def.Id });
    }

    [Theory]
    [MemberData(nameof(GetWidgetDefinitionIds))]
    public void Widget_ObeysLifecycleContract(string widgetId)
    {
        MarkdownTestHost.RunSta(() =>
        {
            var def = WidgetRegistry.Get(widgetId);
            Assert.NotNull(def);

            var tile = new TileModel
            {
                Id = "test_tile_" + Guid.NewGuid().ToString("N"),
                TileType = TileType.Widget,
                TargetPath = def.Id,
                Title = def.DisplayName
            };

            int baselineSubscribers = WidgetHeartbeatService.SubscriberCount;

            // 1. Create through factory (invokes ctor and Initialize)
            IWidgetViewModel vm = def.CreateViewModel(tile);
            Assert.NotNull(vm);

            try
            {
                // 2. Initialize should be callable idempotently
                vm.Initialize(tile);

                // 3. Resume should be idempotent
                vm.Resume();
                vm.Resume();

                // 4. Pulse heartbeat
                WidgetHeartbeatService.Pulse(DateTime.UtcNow);

                // 5. Pause should be idempotent
                vm.Pause();
                vm.Pause();

                // 6. Pulse while paused must not throw
                WidgetHeartbeatService.Pulse(DateTime.UtcNow.AddSeconds(1));
            }
            finally
            {
                // 7. Dispose must be idempotent
                vm.Dispose();
                vm.Dispose();
            }

            // 8. Assert Heartbeat subscriber count returns to baseline
            int afterDisposalSubscribers = WidgetHeartbeatService.SubscriberCount;
            Assert.Equal(baselineSubscribers, afterDisposalSubscribers);

            // 9. Post-disposal heartbeat pulse must not throw
            WidgetHeartbeatService.Pulse(DateTime.UtcNow.AddSeconds(2));
        });
    }

    [Fact]
    public void WidgetRegistry_RegistersAllKnownWidgets()
    {
        var all = WidgetRegistry.GetAll();
        Assert.NotNull(all);
        // Ensure at least the 20 catalog widgets are registered
        Assert.True(all.Count >= 20, $"Expected at least 20 widgets registered, but found {all.Count}");
    }

    [Fact]
    public void WidgetRegistry_ResolvesDefinitionByViewModelType()
    {
        foreach (var def in WidgetRegistry.GetAll())
        {
            bool found = WidgetRegistry.TryGetByViewModelType(def.ViewModelType, out var resolved);
            Assert.True(found, $"Failed to resolve WidgetDefinition for ViewModelType {def.ViewModelType.Name}");
            Assert.NotNull(resolved);
            Assert.Equal(def.Id, resolved.Id);
        }
    }

    [Fact]
    public void WidgetTemplateSelector_ResolvesTemplateForWidgetsWithViewType()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var selector = new WidgetTemplateSelector();

            foreach (var def in WidgetRegistry.GetAll())
            {
                var tile = new TileModel { Id = "test_" + def.Id, TargetPath = def.Id };
                var vm = def.CreateViewModel(tile);

                var template = selector.SelectTemplate(vm, null!);

                if (def.ViewType != null)
                {
                    Assert.NotNull(template);
                    Assert.NotNull(template.VisualTree);
                    Assert.Equal(def.ViewType, template.VisualTree.Type);
                }
            }

            // Normal app tile must return null to fall back to ambient TileModel template
            var appTile = new TileModel { Id = "app_tile", TileType = TileType.App };
            Assert.Null(selector.SelectTemplate(appTile, null!));
        });
    }

    [Theory]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Clock.ClockWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Template.TemplateWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Photos.PhotosWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Weather.WeatherWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Quotes.QuotesWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Rover.RoverWidgetViewModel))]
    public void InteractiveWidgets_ImplementWidgetActionHandler(Type vmType)
    {
        Assert.True(typeof(IWidgetActionHandler).IsAssignableFrom(vmType),
            $"{vmType.Name} must implement IWidgetActionHandler for polymorphic tile activation.");
    }

    [Fact]
    public void CalendarWidget_ImplementsWidgetContextMenuProvider()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var tile = new TileModel { Id = "test_cal", TargetPath = "calendar", SpanX = 8, SpanY = 6 };
            var calVm = new MetroHub.Widgets.Catalog.Calendar.CalendarWidgetViewModel(tile);

            Assert.IsAssignableFrom<IWidgetContextMenuProvider>(calVm);

            // Full size (8x6) provides "Go to Today" menu item
            Assert.True(calVm.IsFullSize);
            var fullSizeItems = calVm.GetContextMenuItems()?.ToList();
            Assert.NotNull(fullSizeItems);
            Assert.Single(fullSizeItems);
            Assert.IsAssignableFrom<System.Windows.Controls.MenuItem>(fullSizeItems[0]);

            // Compact 4x4 does not provide "Go to Today" menu item
            tile.SpanX = 4;
            tile.SpanY = 4;
            Assert.False(calVm.IsFullSize);
            var compactItems = calVm.GetContextMenuItems()?.ToList();
            Assert.NotNull(compactItems);
            Assert.Empty(compactItems);

            calVm.Dispose();
        });
    }

    [Theory]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Calendar.CalendarWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Clock.ClockWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Pomodoro.PomodoroWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Photos.PhotosWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Notepad.NotepadWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Weather.WeatherWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Media.MediaWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Quotes.QuotesWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Habit.HabitWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Dino.DinoWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Rover.RoverWidgetViewModel))]
    [InlineData(typeof(MetroHub.Widgets.Catalog.Template.TemplateWidgetViewModel))]
    public void ContextMenuWidgets_ImplementWidgetContextMenuProvider(Type vmType)
    {
        Assert.True(typeof(IWidgetContextMenuProvider).IsAssignableFrom(vmType),
            $"{vmType.Name} must implement IWidgetContextMenuProvider for polymorphic context menus.");
    }
}

