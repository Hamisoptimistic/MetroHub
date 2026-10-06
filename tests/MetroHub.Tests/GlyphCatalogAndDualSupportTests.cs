using System;
using System.Linq;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controls;
using MetroHub.Widgets.Catalog.Habit;
using MetroHub.Widgets.Serialization;
using Xunit;

namespace MetroHub.Tests;

public sealed class GlyphCatalogAndDualSupportTests
{
    [Theory]
    [InlineData("Desktop24", true)]
    [InlineData("desktop24", true)]
    [InlineData("TargetArrow24", true)]
    [InlineData("Heart24", true)]
    [InlineData("Code24", true)]
    [InlineData("🎯", false)]
    [InlineData("🔥", false)]
    [InlineData("💧", false)]
    [InlineData("CustomText", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void GlyphCatalog_IsSymbol_CorrectlyDistinguishesSymbolsFromEmojis(string? glyph, bool expected)
    {
        bool actual = GlyphCatalog.IsSymbol(glyph);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GlyphCatalog_ContainsValidCuratedSymbolsAndEmojis()
    {
        Assert.NotEmpty(GlyphCatalog.FluentIcons);
        Assert.NotEmpty(GlyphCatalog.Emojis);

        foreach (var item in GlyphCatalog.FluentIcons)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.Glyph));
            Assert.True(GlyphCatalog.IsSymbol(item.Glyph), $"Symbol '{item.Glyph}' must be a valid SymbolRegular name.");
        }

        foreach (var item in GlyphCatalog.Emojis)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.Glyph));
            Assert.False(GlyphCatalog.IsSymbol(item.Glyph), $"Emoji '{item.Glyph}' should not parse as a SymbolRegular symbol.");
        }
    }

    [Fact]
    public void WorkspaceModel_SupportsEmojiGlyphInIconSymbol()
    {
        var ws = new WorkspaceModel
        {
            Name = "Habits & Health",
            IconSymbol = "🔥"
        };

        Assert.Equal("🔥", ws.IconSymbol);
    }

    [Fact]
    public void HabitWidget_SupportsEmojiInSetupAndPersistence()
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
                Id = "habit_emoji_test"
            };

            var vm = new HabitWidgetViewModel(tile, store)
            {
                SetupInputName = "Hydration Habit",
                SetupSelectedIcon = "💧"
            };

            vm.StartHabit();

            Assert.Equal("Hydration Habit", vm.HabitName);
            Assert.Equal("💧", vm.IconSymbol);

            vm.SaveContent();

            string? json = store.Read(HabitWidgetViewModel.WidgetId, tile.Id);
            Assert.NotNull(json);
            var state = WidgetSerializer.Deserialize<HabitWidgetState>(json);
            Assert.NotNull(state);
            Assert.Equal("💧", state!.IconSymbol);
        });
    }
}
