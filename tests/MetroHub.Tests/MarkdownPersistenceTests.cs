using System;
using System.IO;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.Markdown;
using MetroHub.Widgets.Models;
using MetroHub.Widgets.Serialization;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Covers the three-way persistence split: the user's own document, the widget state mirror, and
/// the (now tiny) layout payload. These are the paths that used to lose or mis-file user text.
/// </summary>
public class MarkdownPersistenceTests
{
    private static TileModel NewTile(string? settingsJson = null) => new()
    {
        TileType = TileType.Widget,
        TargetPath = "markdown",
        SpanX = 8,
        SpanY = 4,
        SettingsJson = settingsJson
    };

    [Fact]
    public void StateStore_RoundTrips_RotatesAndPrunes()
    {
        using var sandbox = new MarkdownSandbox();
        var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);

        Assert.Null(store.Read("markdown", "tile1"));

        store.Write("markdown", "tile1", "{\"v\":1}");
        Assert.Equal("{\"v\":1}", store.Read("markdown", "tile1"));

        store.Write("markdown", "tile1", "{\"v\":2}");
        Assert.Equal("{\"v\":2}", store.Read("markdown", "tile1"));

        string bak = Path.Combine(sandbox.BakDir, "markdown", "tile1.json.bak");
        Assert.True(File.Exists(bak), "previous revision should be rotated into the rollover copy");
        Assert.Equal("{\"v\":1}", File.ReadAllText(bak));

        // Read falls back to the rollover copy when the primary is damaged.
        File.WriteAllText(Path.Combine(sandbox.StateDir, "markdown", "tile1.json"), "{}");
        Assert.Equal("{\"v\":1}", store.Read("markdown", "tile1"));

        store.Write("markdown", "tile2", "{\"v\":3}");
        store.PruneExcept("markdown", new[] { "tile1" });
        Assert.NotNull(store.Read("markdown", "tile1"));
        Assert.Null(store.Read("markdown", "tile2"));
    }

    [Fact]
    public void EditingTwoTabs_WritesBothDocuments_AndKeepsTheLayoutPayloadSlim()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            string fileA = sandbox.WriteDocument("a.md", "old A");
            var model = NewTile();
            var vm = new MarkdownWidgetViewModel(model, store);

            vm.CurrentTab!.SourceFilePath = fileA;
            vm.CurrentTab.SourceFileWriteUtc = new FileInfo(fileA).LastWriteTimeUtc.Ticks;
            vm.MarkdownText = "edited A";

            vm.NewTab();
            vm.MarkdownText = "edited B";

            // Lifecycle flush (what the hub calls on hide / exit).
            vm.SaveSettings();

            Assert.Equal("edited A", File.ReadAllText(fileA));
            Assert.Equal("edited B", vm.Tabs[1].Text);

            string? stateJson = store.Read(MarkdownWidgetViewModel.WidgetId, model.Id);
            Assert.NotNull(stateJson);
            Assert.Contains("edited A", stateJson);
            Assert.Contains("edited B", stateJson);

            Assert.NotNull(model.SettingsJson);
            Assert.Contains("schemaVersion", model.SettingsJson!);
            Assert.DoesNotContain("edited A", model.SettingsJson!);
            Assert.DoesNotContain("edited B", model.SettingsJson!);
        });
    }

    [Fact]
    public void BackgroundTabEdits_AreNotAbandonedByAnotherTabsWriteBack()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            string fileA = sandbox.WriteDocument("a.md", "old A");
            string fileB = sandbox.WriteDocument("b.md", "old B");
            var vm = new MarkdownWidgetViewModel(NewTile(), store);

            vm.CurrentTab!.SourceFilePath = fileA;
            vm.CurrentTab.SourceFileWriteUtc = new FileInfo(fileA).LastWriteTimeUtc.Ticks;
            vm.MarkdownText = "edited A";

            vm.NewTab();
            vm.CurrentTab!.SourceFilePath = fileB;
            vm.CurrentTab.SourceFileWriteUtc = new FileInfo(fileB).LastWriteTimeUtc.Ticks;
            vm.MarkdownText = "edited B";

            vm.SaveSettings();

            // Both documents land: a per-tab flag, not one flag for the whole widget.
            Assert.Equal("edited A", File.ReadAllText(fileA));
            Assert.Equal("edited B", File.ReadAllText(fileB));
            Assert.False(vm.HasUnsavedDocuments);
        });
    }

    [Fact]
    public void ClosingATab_FlushesItsDocument()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            string file = sandbox.WriteDocument("close.md", "old");
            var vm = new MarkdownWidgetViewModel(NewTile(), store);

            var tab = vm.CurrentTab!;
            tab.SourceFilePath = file;
            tab.SourceFileWriteUtc = new FileInfo(file).LastWriteTimeUtc.Ticks;
            vm.MarkdownText = "edited then closed";

            vm.CloseTab(tab);

            Assert.Equal("edited then closed", File.ReadAllText(file));
            Assert.DoesNotContain(tab, vm.Tabs);
        });
    }

    [Fact]
    public void ClosingATab_IsRefusedWhenTheFileCannotBeWritten()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            string file = sandbox.WriteDocument("locked.md", "on disk");
            var vm = new MarkdownWidgetViewModel(NewTile(), store);

            var tab = vm.CurrentTab!;
            tab.SourceFilePath = file;
            tab.SourceFileWriteUtc = new FileInfo(file).LastWriteTimeUtc.Ticks;
            vm.MarkdownText = "unsaved edit";

            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                vm.CloseTab(tab);
            }

            // Editing text must never be dropped: the tab stays and says why.
            Assert.Contains(tab, vm.Tabs);
            Assert.Equal("unsaved edit", tab.Text);
            Assert.Contains("kept", vm.StatusMessage);
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
                "{\"tabs\":[{\"id\":\"t1\",\"title\":\"legacy.md\",\"text\":\"hello legacy\",\"sourceFilePath\":null}]," +
                "\"activeTabIndex\":0,\"activeMode\":\"Write\"}";
            var model = NewTile(legacy);

            var vm = new MarkdownWidgetViewModel(model, store);
            Assert.Equal("hello legacy", vm.MarkdownText);

            vm.SaveSettings();

            string? stateJson = store.Read(MarkdownWidgetViewModel.WidgetId, model.Id);
            Assert.NotNull(stateJson);
            var state = WidgetSerializer.Deserialize<MarkdownWidgetState>(stateJson);
            Assert.NotNull(state);
            Assert.Equal("hello legacy", state!.Tabs[0].Text);

            Assert.NotNull(model.SettingsJson);
            Assert.DoesNotContain("hello legacy", model.SettingsJson!);
            Assert.Contains("stateRef", model.SettingsJson!);
        });
    }

    [Fact]
    public void LegacySingleNotePayload_MigratesToo()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            string file = sandbox.WriteDocument("note.md", "v1 body");
            string legacy = $"{{\"markdownText\":\"v1 body\",\"activeTab\":\"Read\",\"sourceFilePath\":\"{file.Replace("\\", "\\\\")}\"}}";

            var vm = new MarkdownWidgetViewModel(NewTile(legacy), store);

            Assert.Equal("v1 body", vm.MarkdownText);
            Assert.True(vm.IsReadTab); // legacy "activeTab": "Read" maps onto preview mode
            Assert.Equal(file, vm.SourceFilePath);
        });
    }

    [Fact]
    public void StateFileWinsOverALegacyLayoutPayload()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile();
            store.Write(MarkdownWidgetViewModel.WidgetId, model.Id, WidgetSerializer.Serialize(new MarkdownWidgetState
            {
                Tabs = { new DocumentTabItem("new.md", "from state file") }
            }));

            model.SettingsJson =
                "{\"tabs\":[{\"id\":\"t1\",\"title\":\"old.md\",\"text\":\"stale inline copy\"}]}";
            var vm = new MarkdownWidgetViewModel(model, store);

            Assert.Equal("from state file", vm.MarkdownText);
        });
    }

    [Fact]
    public void NewTab_RespectsTheCap()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var vm = new MarkdownWidgetViewModel(NewTile(), store);

            for (int i = 0; i < MarkdownWidgetViewModel.MaxTabs + 3; i++)
            {
                vm.NewTab();
            }

            Assert.Equal(MarkdownWidgetViewModel.MaxTabs, vm.Tabs.Count);
            Assert.Contains("Maximum", vm.StatusMessage);
            Assert.False(vm.CanAddTab);
        });
    }

    [Fact]
    public void SwitchingDocuments_RaisesTheUndoResetSignal()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var vm = new MarkdownWidgetViewModel(NewTile(), store);

            int signals = 0;
            vm.ActiveDocumentReplaced += (_, _) => signals++;

            var first = vm.CurrentTab!;
            vm.NewTab();
            vm.SelectTab(first);

            // The view clears the shared editor's undo history on each of these: without it Ctrl+Z
            // would paste one document's text into the other.
            Assert.Equal(2, signals);
        });
    }

    [Fact]
    public void Autosave_DoesNotTouchTheLayoutPayload()
    {
        MarkdownTestHost.RunSta(() =>
        {
            using var sandbox = new MarkdownSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var model = NewTile();
            var vm = new MarkdownWidgetViewModel(model, store);

            vm.SaveSettings(); // settles the layout pointer once
            string layoutAfterLoad = model.SettingsJson!;

            vm.MarkdownText = "typing happens here";
            vm.SaveContent(); // the idle autosave path

            Assert.Equal(layoutAfterLoad, model.SettingsJson);
            Assert.Equal("typing happens here", vm.CurrentTab!.Text);
        });
    }
}
