using System;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controls;
using MetroHub.Widgets.Catalog.Habit;
using MetroHub.Widgets.Serialization;
using Xunit;

namespace MetroHub.Tests;

public sealed class GlyphPickerTests
{
    [Fact]
    public void GlyphPickerControl_InitializesWithDefaultDesktopIcon()
    {
        WpfTestHost.RunSta(() =>
        {
            var picker = new GlyphPickerControl();

            Assert.Equal("Desktop24", picker.SelectedGlyph);
        });
    }

    [Fact]
    public void GlyphPickerControl_AllowsChangingSelectedGlyph()
    {
        WpfTestHost.RunSta(() =>
        {
            var picker = new GlyphPickerControl();

            picker.SelectedGlyph = "Rocket24";
            Assert.Equal("Rocket24", picker.SelectedGlyph);

            picker.SelectedGlyph = "Code24";
            Assert.Equal("Code24", picker.SelectedGlyph);
        });
    }

    [Fact]
    public void WorkspaceModel_SupportsFluentIconSymbol()
    {
        var ws = new WorkspaceModel
        {
            Name = "Development",
            IconSymbol = "Code24"
        };

        Assert.Equal("Code24", ws.IconSymbol);
    }

    [Fact]
    public void HabitWidget_SupportsFluentIconInSetupAndPersistence()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var tile = new TileModel
            {
                TileType = TileType.Widget,
                TargetPath = "habit",
                SpanX = 8,
                SpanY = 6,
                Id = "habit_icon_test"
            };

            var vm = new HabitWidgetViewModel(tile, store)
            {
                SetupInputName = "Coding Practice",
                SetupSelectedIcon = "Code24"
            };

            vm.StartHabit();

            Assert.Equal("Coding Practice", vm.HabitName);
            Assert.Equal("Code24", vm.IconSymbol);

            vm.SaveContent();

            string? json = store.Read(HabitWidgetViewModel.WidgetId, tile.Id);
            Assert.NotNull(json);
            var state = WidgetSerializer.Deserialize<HabitWidgetState>(json);
            Assert.NotNull(state);
            Assert.Equal("Code24", state!.IconSymbol);
        });
    }
}
