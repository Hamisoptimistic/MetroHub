using System;
using System.IO;
using System.Linq;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.Habit;
using MetroHub.Widgets.Serialization;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Guards the persistence split for the Habit tracker. Regression target: every day toggle — and
/// every hub hide — used to write the whole hub layout (twice, forced to disk) on the UI thread.
/// </summary>
public class HabitPersistenceTests
{
    private static TileModel NewTile(string? settingsJson = null, string? id = null)
    {
        var tile = new TileModel
        {
            TileType = TileType.Widget,
            TargetPath = "habit",
            SpanX = 8,
            SpanY = 6,
            SettingsJson = settingsJson
        };
        if (id != null)
        {
            tile.Id = id;
        }
        return tile;
    }

    [Fact]
    public void TogglingADay_WritesOnlyTheStateFile_AndLeavesTheLayoutPayloadAlone()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile(id: "habit1");
            var vm = new HabitWidgetViewModel(model, store);
            vm.HabitName = "Read";
            vm.SaveSettings();                       // settles the layout pointer once
            string layout = model.SettingsJson!;

            var today = vm.Days.First(d => d.IsToday && d.IsClickable);
            vm.SetDayState(today, HabitDayState.Done);

            string? stateJson = store.Read(HabitWidgetViewModel.WidgetId, model.Id);
            Assert.NotNull(stateJson);
            var state = WidgetSerializer.Deserialize<HabitWidgetState>(stateJson);
            Assert.NotNull(state);
            Assert.True(state!.DayStates.ContainsKey(today.DateKey));

            // A checkmark must not re-serialize every widget's payload in the hub layout.
            Assert.Equal(layout, model.SettingsJson);
            Assert.Contains("stateRef", model.SettingsJson!);
            Assert.DoesNotContain("\"dayStates\"", model.SettingsJson!);
        });
    }

    [Fact]
    public void HabitGrid_RoundTripsThroughTheStateFile()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile(id: "habit2");
            var vm = new HabitWidgetViewModel(model, store);

            vm.HabitName = "Stretch";
            vm.IconSymbol = "Heart24";
            int todayKey = DateTime.Today.Year * 10000 + DateTime.Today.Month * 100 + DateTime.Today.Day;
            vm.SetDayState(vm.Days.First(d => d.IsToday && d.IsClickable), HabitDayState.Done);
            vm.SaveSettings();

            var reloaded = new HabitWidgetViewModel(NewTile(model.SettingsJson, "habit2"), store);

            Assert.Equal("Stretch", reloaded.HabitName);
            Assert.Equal("Heart24", reloaded.IconSymbol);
            Assert.False(reloaded.IsSetupMode);
            Assert.Contains(reloaded.Days, d => d.DateKey == todayKey && d.State == HabitDayState.Done);
        });
    }

    [Fact]
    public void LegacyPascalCasePayload_MigratesIntoTheStateFile_AndLeavesTheLayout()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);

            int todayKey = DateTime.Today.Year * 10000 + DateTime.Today.Month * 100 + DateTime.Today.Day;
            string legacy =
                "{\"HabitName\":\"Legacy habit\",\"IconSymbol\":\"TargetArrow24\",\"AccentColorHex\":\"#0B9E76\"," +
                $"\"DayStates\":{{\"{todayKey}\":1}}}}";
            var model = NewTile(legacy, "habit3");

            var vm = new HabitWidgetViewModel(model, store);
            Assert.Equal("Legacy habit", vm.HabitName);
            Assert.Contains(vm.Days, d => d.DateKey == todayKey && d.State == HabitDayState.Done);

            vm.SaveSettings();

            string? stateJson = store.Read(HabitWidgetViewModel.WidgetId, model.Id);
            Assert.NotNull(stateJson);
            var state = WidgetSerializer.Deserialize<HabitWidgetState>(stateJson);
            Assert.NotNull(state);
            Assert.Equal("Legacy habit", state!.HabitName);
            Assert.True(state.DayStates.ContainsKey(todayKey));

            Assert.NotNull(model.SettingsJson);
            Assert.DoesNotContain("Legacy habit", model.SettingsJson!);
            Assert.Contains("stateRef", model.SettingsJson!);
        });
    }

    [Fact]
    public void StateFileWinsOverALegacyLayoutPayload()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile(id: "habit4");

            store.Write(
                HabitWidgetViewModel.WidgetId,
                model.Id,
                WidgetSerializer.Serialize(new HabitWidgetState { HabitName = "from state file" }));
            model.SettingsJson = "{\"HabitName\":\"stale inline copy\"}";

            var vm = new HabitWidgetViewModel(model, store);

            Assert.Equal("from state file", vm.HabitName);
        });
    }

    [Fact]
    public void RepeatedLifecycleSaves_DoNotRewriteTheLayoutPayload()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile(id: "habit5");
            var vm = new HabitWidgetViewModel(model, store);

            vm.HabitName = "Meditate";
            vm.SaveSettings();
            string layout = model.SettingsJson!;

            // Every hub hide calls Pause(); without the pointer diff-guard each one re-serialized
            // and fsync'd the whole layout.
            vm.Pause();
            vm.Pause();

            Assert.Equal(layout, model.SettingsJson);
        });
    }

    [Fact]
    public void UnchangedState_IsNotRewritten()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile(id: "habit6");
            var vm = new HabitWidgetViewModel(model, store);

            vm.HabitName = "Journal";
            vm.SaveContent();

            string path = Path.Combine(sandbox.StateDir, HabitWidgetViewModel.WidgetId, model.Id + ".json");
            DateTime firstWrite = File.GetLastWriteTimeUtc(path);

            vm.SaveContent(); // nothing changed → must be a no-op
            vm.SaveContent();

            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(path));
        });
    }
}
