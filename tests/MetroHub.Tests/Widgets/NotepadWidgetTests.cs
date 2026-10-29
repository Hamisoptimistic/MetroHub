using System.Linq;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets;
using MetroHub.Widgets.Catalog.Notepad;
using MetroHub.Widgets.Serialization;
using Xunit;

namespace MetroHub.Tests;

public class NotepadWidgetTests
{
    private static TileModel CreateTestTile(string? settingsJson = null, string? id = null)
    {
        return new TileModel
        {
            Id = id ?? System.Guid.NewGuid().ToString("N"),
            TileType = TileType.Widget,
            TargetPath = "notepad",
            SpanX = 8,
            SpanY = 6,
            SettingsJson = settingsJson
        };
    }

    [Fact]
    public void Initializes_WithDefaultSettings_WhenJsonIsNull()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new NotepadWidgetViewModel(tile);

            Assert.Equal(NotepadMode.Notes, vm.CurrentMode);
            Assert.True(vm.IsNotesMode);
            Assert.False(vm.IsTodoMode);
            Assert.False(vm.IsHistoryView);
            Assert.True(vm.IsWordWrapEnabled);
            Assert.Empty(vm.PlainText);
            Assert.Equal(0, vm.WordCount);
            Assert.Equal(0, vm.CharCount);
            Assert.Equal(3, vm.AllowedSizes.Count);
            Assert.Contains(WidgetSize.Huge, vm.AllowedSizes);
            Assert.Contains(WidgetSize.Canvas, vm.AllowedSizes);
            Assert.Contains(WidgetSize.Full, vm.AllowedSizes);
            Assert.DoesNotContain(WidgetSize.Wide, vm.AllowedSizes);
            Assert.DoesNotContain(WidgetSize.Large, vm.AllowedSizes);
        });
    }

    [Fact]
    public void ToggleTodoMode_SwitchesBetweenNotesAndTodo()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new NotepadWidgetViewModel(tile);

            vm.ToggleTodoMode();
            Assert.Equal(NotepadMode.Todo, vm.CurrentMode);
            Assert.True(vm.IsTodoMode);
            Assert.False(vm.IsNotesMode);
            Assert.True(vm.IsActiveTodoView);
            Assert.False(vm.IsHistoryTodoView);

            vm.ToggleTodoMode();
            Assert.Equal(NotepadMode.Notes, vm.CurrentMode);
            Assert.True(vm.IsNotesMode);

            // SwitchToNotesMode from Todo mode
            vm.ToggleTodoMode();
            Assert.True(vm.IsTodoMode);
            vm.SwitchToNotesMode();
            Assert.True(vm.IsNotesMode);
            Assert.False(vm.IsTodoMode);
        });
    }

    [Fact]
    public void AddTodoCommand_AddsNewActiveTask_AndClearsInput()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new NotepadWidgetViewModel(tile);

            vm.NewTodoText = "Test Task 1";
            vm.AddTodo();

            Assert.Single(vm.ActiveTodos);
            Assert.Equal("Test Task 1", vm.ActiveTodos[0].Text);
            Assert.False(vm.ActiveTodos[0].IsCompleted);
            Assert.Empty(vm.NewTodoText);
        });
    }

    [Fact]
    public void ToggleTodoCompletion_MovesTaskBetweenActiveAndCompleted()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new NotepadWidgetViewModel(tile);

            vm.NewTodoText = "Task To Complete";
            vm.AddTodo();
            var task = vm.ActiveTodos[0];

            // Mark as complete
            task.IsCompleted = true;
            vm.ToggleTodoCompletion(task);

            Assert.Empty(vm.ActiveTodos);
            Assert.Single(vm.CompletedTodos);
            Assert.Equal(1, vm.CompletedCount);
            Assert.NotNull(task.CompletedAt);

            // Uncheck in history -> moves back to active
            task.IsCompleted = false;
            vm.ToggleTodoCompletion(task);

            Assert.Single(vm.ActiveTodos);
            Assert.Empty(vm.CompletedTodos);
            Assert.Equal(0, vm.CompletedCount);
            Assert.Null(task.CompletedAt);
        });
    }

    [Fact]
    public void DeleteTodoCommand_RemovesTask()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new NotepadWidgetViewModel(tile);

            vm.NewTodoText = "Task To Delete";
            vm.AddTodo();
            var task = vm.ActiveTodos[0];

            vm.DeleteTodo(task);
            Assert.Empty(vm.ActiveTodos);
        });
    }

    [Fact]
    public void UpdateTextMetrics_ComputesWordAndCharCountsCorrectly()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new NotepadWidgetViewModel(tile);

            vm.UpdateTextMetrics("The quick brown fox jumps over the lazy dog.");
            Assert.Equal(9, vm.WordCount);
            Assert.Equal(44, vm.CharCount);
        });
    }

    [Fact]
    public void SaveSettings_PersistsToJson_AndSurvivesRoundTrip()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using (var vm = new NotepadWidgetViewModel(tile))
            {
                vm.UpdateTextMetrics("Persistent text", "<FlowDocument/>");
                vm.NewTodoText = "Persistent task";
                vm.AddTodo();
                vm.Save();
            }

            // Restore in fresh ViewModel instance
            using (var vm2 = new NotepadWidgetViewModel(tile))
            {
                Assert.Equal("Persistent text", vm2.PlainText);
                Assert.Equal("<FlowDocument/>", vm2.DocumentXaml);
                Assert.Single(vm2.ActiveTodos);
                Assert.Equal("Persistent task", vm2.ActiveTodos[0].Text);
            }
        });
    }

    [Fact]
    public void Pause_SavesPendingSettingsOnMinimize()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using (var vm = new NotepadWidgetViewModel(tile))
            {
                vm.UpdateTextMetrics("Minimized text");
                vm.Pause(); // Triggered when MetroHub hides/minimizes to tray
            }

            string? stateJson = WidgetStateStore.Default.Read(NotepadWidgetViewModel.WidgetId, tile.Id);
            Assert.NotNull(stateJson);
            Assert.Contains("Minimized text", stateJson);
        });
    }

    [Fact]
    public void Dispose_ClearsCollectionsAndCleansUp()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            var vm = new NotepadWidgetViewModel(tile);
            vm.NewTodoText = "Cleanup task";
            vm.AddTodo();

            vm.Dispose();

            Assert.Empty(vm.ActiveTodos);
            Assert.Empty(vm.CompletedTodos);
        });
    }

    [Fact]
    public void CorruptedMode_FallsBackToNotesMode()
    {
        WpfTestHost.RunSta(() =>
        {
            var settings = new NotepadWidgetSettings();
            settings.Mode = (NotepadMode)99;
            Assert.Equal(NotepadMode.Notes, settings.Mode);

            var json = "{\"Mode\": 99, \"PlainText\": \"Corrupted test\"}";
            var tile = CreateTestTile(json);
            using var vm = new NotepadWidgetViewModel(tile);
            Assert.Equal(NotepadMode.Notes, vm.CurrentMode);
            Assert.True(vm.IsNotesMode);
        });
    }

    [Fact]
    public void SaveSettings_InvokesRequestDocumentSync_SynchronizingDocumentXaml()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new NotepadWidgetViewModel(tile);

            bool syncInvoked = false;
            vm.RequestDocumentSync = () =>
            {
                syncInvoked = true;
                vm.UpdateTextMetrics("Synced text", "<FlowDocument><Paragraph>Synced</Paragraph></FlowDocument>");
            };

            // Calling SaveSettings triggers sync
            vm.SaveSettings();

            Assert.True(syncInvoked);
            Assert.Equal("<FlowDocument><Paragraph>Synced</Paragraph></FlowDocument>", vm.DocumentXaml);
            string? stateJson = WidgetStateStore.Default.Read(NotepadWidgetViewModel.WidgetId, tile.Id);
            Assert.NotNull(stateJson);
            Assert.Contains("Synced text", stateJson);
            Assert.Contains("Synced", stateJson);
        });
    }

    [Fact]
    public void Pause_TriggersRequestDocumentSync_BeforePersisting()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new NotepadWidgetViewModel(tile);

            vm.RequestDocumentSync = () =>
            {
                vm.UpdateTextMetrics("Text while minimizing", "<FlowDocument>Minimizing</FlowDocument>");
            };

            vm.Pause();

            Assert.Equal("<FlowDocument>Minimizing</FlowDocument>", vm.DocumentXaml);
            string? stateJson = WidgetStateStore.Default.Read(NotepadWidgetViewModel.WidgetId, tile.Id);
            Assert.NotNull(stateJson);
            Assert.Contains("Text while minimizing", stateJson);
            Assert.Contains("Minimizing", stateJson);
        });
    }

    [Fact]
    public void ToggleTodoMode_TriggersRequestDocumentSync_BeforeModeChanges()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new NotepadWidgetViewModel(tile);

            bool syncInvoked = false;
            NotepadMode? modeDuringFirstSync = null;

            vm.RequestDocumentSync = () =>
            {
                syncInvoked = true;
                modeDuringFirstSync ??= vm.CurrentMode;
                vm.UpdateTextMetrics("Text before todo mode", "<FlowDocument>Pre-Todo</FlowDocument>");
            };

            vm.ToggleTodoMode();

            Assert.True(syncInvoked);
            Assert.Equal(NotepadMode.Notes, modeDuringFirstSync);
            Assert.Equal(NotepadMode.Todo, vm.CurrentMode);
            Assert.Equal("<FlowDocument>Pre-Todo</FlowDocument>", vm.DocumentXaml);
        });
    }

    [Fact]
    public void View_LoadDocumentFromViewModel_FallsBackToPlainText_WhenXamlEmpty()
    {
        WpfTestHost.RunSta(() =>
        {
            var json = "{\"Mode\": 0, \"PlainText\": \"Fallback plain note\", \"DocumentXaml\": \"\"}";
            var tile = CreateTestTile(json);
            using var vm = new NotepadWidgetViewModel(tile);
            Assert.Equal("Fallback plain note", vm.PlainText); // Step 1: VM initialization

            var view = new NotepadWidgetView
            {
                DataContext = vm
            };
            Assert.Equal("Fallback plain note", vm.PlainText); // Step 2: DataContext set

            // Loading view with PlainText populated but DocumentXaml empty
            var loadedArgs = new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.LoadedEvent, view);
            view.RaiseEvent(loadedArgs);
            Assert.Equal("Fallback plain note", vm.PlainText); // Step 3: View loaded

            // Trigger sync (as on pause or unload)
            vm.RequestDocumentSync?.Invoke();

            // Verify PlainText was not wiped out
            Assert.Equal("Fallback plain note", vm.PlainText); // Step 4: After sync
            Assert.Contains("Fallback plain note", vm.DocumentXaml);
        });
    }

    [Fact]
    public void View_MultipleLoadedEvents_DoNotThrowOrCorruptState()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new NotepadWidgetViewModel(tile);

            var view = new NotepadWidgetView
            {
                DataContext = vm
            };

            var loadedArgs = new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.LoadedEvent, view);
            view.RaiseEvent(loadedArgs);
            view.RaiseEvent(loadedArgs); // Multiple Loaded events (e.g. virtualization re-entry)

            Assert.NotNull(vm.RequestDocumentSync);

            var unloadedArgs = new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.UnloadedEvent, view);
            view.RaiseEvent(unloadedArgs);

            Assert.Null(vm.RequestDocumentSync);
        });
    }

    [Fact]
    public void LegacySettingsJson_MigratesToStateFile_AndClearsFromModel()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var legacyJson = "{\"Mode\":1,\"PlainText\":\"Migrated note\",\"ActiveTodos\":[{\"Text\":\"Migrated todo\",\"IsCompleted\":false}]}";
            var tile = CreateTestTile(legacyJson);
            tile.Id = "migrate_test_1";

            using (var vm = new NotepadWidgetViewModel(tile, store))
            {
                Assert.Equal(NotepadMode.Todo, vm.CurrentMode);
                Assert.Equal("Migrated note", vm.PlainText);
                Assert.Single(vm.ActiveTodos);
                Assert.Equal("Migrated todo", vm.ActiveTodos[0].Text);

                vm.SaveSettings();
            }

            // Verify written to state file
            string? stateJson = store.Read(NotepadWidgetViewModel.WidgetId, "migrate_test_1");
            Assert.NotNull(stateJson);
            Assert.Contains("Migrated note", stateJson);
            Assert.Contains("Migrated todo", stateJson);

            // Verify cleared from tile model so layout.json does not retain redundant payload
            Assert.Null(tile.SettingsJson);
        });
    }

    [Fact]
    public void WidgetStateStore_IsolatedInstances_DoNotInterfere()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var store = new WidgetStateStore(sandbox.StateDir, sandbox.BakDir);
            var tile1 = CreateTestTile(null);
            tile1.Id = "note_instance_A";
            var tile2 = CreateTestTile(null);
            tile2.Id = "note_instance_B";

            using (var vmA = new NotepadWidgetViewModel(tile1, store))
            {
                vmA.UpdateTextMetrics("Text for Instance A");
                vmA.SaveSettings();
            }

            using (var vmB = new NotepadWidgetViewModel(tile2, store))
            {
                vmB.UpdateTextMetrics("Text for Instance B");
                vmB.SaveSettings();
            }

            string? stateA = store.Read(NotepadWidgetViewModel.WidgetId, "note_instance_A");
            string? stateB = store.Read(NotepadWidgetViewModel.WidgetId, "note_instance_B");

            Assert.NotNull(stateA);
            Assert.NotNull(stateB);
            Assert.Contains("Text for Instance A", stateA);
            Assert.DoesNotContain("Text for Instance B", stateA);
            Assert.Contains("Text for Instance B", stateB);
            Assert.DoesNotContain("Text for Instance A", stateB);
        });
    }
}


