using System.IO;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.Notepad;
using MetroHub.Widgets.Serialization;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Guards the persistence split: the note body and to-do list belong in the widget state file,
/// never in <c>layout.json</c>. Regression target — a typing pause used to re-serialize and
/// fsync the entire hub layout (every widget's payload) on the UI thread.
/// </summary>
public class NotepadPersistenceTests
{
    private static TileModel NewTile(string? settingsJson = null, string? id = null)
    {
        var tile = new TileModel
        {
            TileType = TileType.Widget,
            TargetPath = "notepad",
            SpanX = 8,
            SpanY = 4,
            SettingsJson = settingsJson
        };
        if (id != null)
        {
            tile.Id = id;
        }
        return tile;
    }

    [Fact]
    public void Typing_WritesOnlyTheStateFile_AndLeavesTheLayoutPayloadAlone()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile(id: "notepad1");
            var vm = new NotepadWidgetViewModel(model, store);

            vm.SaveSettings();                       // settles the layout pointer once
            string layout = model.SettingsJson!;

            vm.NoteText = "hello from the notes widget";
            vm.SaveContent();                        // the debounced autosave path

            string? stateJson = store.Read(NotepadWidgetViewModel.WidgetId, model.Id);
            Assert.NotNull(stateJson);
            Assert.Contains("hello from the notes widget", stateJson);

            // The whole point of the split: typing must not re-serialize every widget's payload.
            Assert.Equal(layout, model.SettingsJson);
            Assert.DoesNotContain("hello from the notes widget", model.SettingsJson!);
            Assert.Contains("stateRef", model.SettingsJson!);
        });
    }

    [Fact]
    public void NoteAndTasks_RoundTripThroughTheStateFile()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile(id: "notepad2");
            var vm = new NotepadWidgetViewModel(model, store);

            vm.NoteText = "round trip body";
            vm.ActiveViewMode = "Todo";
            vm.NewTaskText = "buy milk";
            vm.AddTask();
            vm.SaveSettings();

            var reloaded = new NotepadWidgetViewModel(NewTile(model.SettingsJson, "notepad2"), store);

            Assert.Equal("round trip body", reloaded.NoteText);
            Assert.True(reloaded.IsTodoView);
            Assert.Single(reloaded.Tasks);
            Assert.Equal("buy milk", reloaded.Tasks[0].Text);
        });
    }

    [Fact]
    public void LegacyInlinePayload_MigratesIntoTheStateFile_AndLeavesTheLayout()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            const string legacy =
                "{\"noteText\":\"legacy note body\",\"activeViewMode\":\"Todo\"," +
                "\"tasks\":[{\"text\":\"old task\",\"isCompleted\":true}]}";
            var model = NewTile(legacy, "notepad3");

            var vm = new NotepadWidgetViewModel(model, store);
            Assert.Equal("legacy note body", vm.NoteText);
            Assert.True(vm.IsTodoView);
            Assert.Single(vm.Tasks);

            vm.SaveSettings();

            string? stateJson = store.Read(NotepadWidgetViewModel.WidgetId, model.Id);
            Assert.NotNull(stateJson);
            var state = WidgetSerializer.Deserialize<NotepadWidgetState>(stateJson);
            Assert.NotNull(state);
            Assert.Equal("legacy note body", state!.NoteText);

            Assert.NotNull(model.SettingsJson);
            Assert.DoesNotContain("legacy note body", model.SettingsJson!);
            Assert.Contains("stateRef", model.SettingsJson!);
        });
    }

    [Fact]
    public void StateFileWinsOverALegacyLayoutPayload()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile(id: "notepad4");

            store.Write(
                NotepadWidgetViewModel.WidgetId,
                model.Id,
                WidgetSerializer.Serialize(new NotepadWidgetState { NoteText = "from state file" }));
            model.SettingsJson = "{\"noteText\":\"stale inline copy\"}";

            var vm = new NotepadWidgetViewModel(model, store);

            Assert.Equal("from state file", vm.NoteText);
        });
    }

    [Fact]
    public void HidingTheHub_FlushesPendingEdits_WithoutRewritingLayout()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile(id: "notepad5");
            var vm = new NotepadWidgetViewModel(model, store);

            vm.SaveSettings();
            string layout = model.SettingsJson!;

            vm.NoteText = "typed, then the hub hid";
            vm.Pause();                              // Pause() is the hub-hide lifecycle hook

            string? stateJson = store.Read(NotepadWidgetViewModel.WidgetId, model.Id);
            Assert.NotNull(stateJson);
            Assert.Contains("typed, then the hub hid", stateJson);
            Assert.Equal(layout, model.SettingsJson);
        });
    }

    [Fact]
    public void UnchangedState_IsNotRewritten()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile(id: "notepad6");
            var vm = new NotepadWidgetViewModel(model, store);

            vm.NoteText = "stable";
            vm.SaveContent();

            string path = Path.Combine(sandbox.StateDir, NotepadWidgetViewModel.WidgetId, model.Id + ".json");
            DateTime firstWrite = File.GetLastWriteTimeUtc(path);

            vm.SaveContent(); // nothing changed → must be a no-op
            vm.SaveContent();

            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(path));
        });
    }
}
