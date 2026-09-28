using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Notepad;

/// <summary>
/// ViewModel for the Notes & Tasks (Notepad / To-Do) Widget.
/// Supports 8x4 (Mega), 8x6 (Huge), 8x8 (Canvas), and 8x10 (Full) grid dimensions.
/// Implements debounced auto-saving, dual-mode view switching, smart list transformation,
/// and reactive to-do task management.
/// <para>
/// Persistence is split two ways: the note body and to-do list live in the widget state file
/// (<c>config\widgets\notepad\{tileId}.json</c>, written on an idle debounce), while
/// <c>layout.json</c> only ever holds a slim schema + state pointer. That split is what keeps a
/// typing pause from re-serializing and fsync-ing the whole hub layout.
/// </para>
/// </summary>
public sealed partial class NotepadWidgetViewModel : WidgetViewModelBase
{
    /// <summary>Registry id of this widget (state-file directory + tile target path).</summary>
    public const string WidgetId = "notepad";

    /// <summary>Idle delay before an autosave writes the widget state file.</summary>
    private const int ContentSaveDelayMs = 400;

    /// <summary>Idle delay before a layout-pointer change reaches layout.json.</summary>
    private const int LayoutSaveDelayMs = 400;

    private readonly IWidgetStateStore _stateStore;
    private DispatcherTimer? _debounceTimer;
    private DispatcherTimer? _layoutTimer;
    private bool _isSettingsLoaded;
    private string? _lastSavedStateJson;    // state-file payload from the last write (skip unchanged)
    private bool _layoutStubPersisted;      // SettingsJson already carries the slim schema-2 pointer
    private bool _legacyPayloadPresent;     // layout.json still holds the inline note → rewrite once
    private bool _stateNeedsInitialWrite;   // no state file yet (first run or fresh migration)

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Mega,   // 8x4
        WidgetSize.Huge,   // 8x6
        WidgetSize.Canvas, // 8x8
        WidgetSize.Full    // 8x10
    };

    #region Observable Properties

    [ObservableProperty]
    private string _noteText = string.Empty;

    [ObservableProperty]
    private string _activeViewMode = "Notes"; // "Notes" or "Todo"

    [ObservableProperty]
    private string _activeListType = "None"; // "None", "Bullet", "Numbered"

    [ObservableProperty]
    private string _newTaskText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<TodoTaskItem> _tasks = new();

    #endregion

    #region Computed View Properties

    public bool IsNotesView => string.Equals(ActiveViewMode, "Notes", StringComparison.OrdinalIgnoreCase);

    public bool IsTodoView => string.Equals(ActiveViewMode, "Todo", StringComparison.OrdinalIgnoreCase);

    public bool IsBulletActive => string.Equals(ActiveListType, "Bullet", StringComparison.OrdinalIgnoreCase);

    public bool IsNumberedActive => string.Equals(ActiveListType, "Numbered", StringComparison.OrdinalIgnoreCase);

    public bool IsBulletActiveInNotes => IsNotesView && IsBulletActive;

    public bool IsNumberedActiveInNotes => IsNotesView && IsNumberedActive;

    public string? CurrentTileSelection =>
        IsTodoView ? "Todo" :
        IsBulletActiveInNotes ? "Bullets" :
        IsNumberedActiveInNotes ? "Numbered" : null;

    public int RemainingTasksCount => Tasks.Count(t => !t.IsCompleted);

    public int CompletedTasksCount => Tasks.Count(t => t.IsCompleted);

    public int TotalTasksCount => Tasks.Count;

    public bool HasTasks => Tasks.Count > 0;

    public bool HasCompletedTasks => Tasks.Any(t => t.IsCompleted);

    public string TasksSummary => $"{RemainingTasksCount} remaining • {CompletedTasksCount} done";

    public bool IsMegaSize => Model.SpanX == 8 && Model.SpanY == 4;

    public bool IsHugeSize => Model.SpanX == 8 && Model.SpanY == 6;

    public bool IsCanvasSize => Model.SpanX == 8 && Model.SpanY == 8;

    public bool IsFullSize => Model.SpanX == 8 && Model.SpanY == 10;

    #endregion

    public NotepadWidgetViewModel(TileModel model) : this(model, WidgetStateStore.Default)
    {
    }

    /// <summary>Test seam: inject a state store rooted somewhere other than %LocalAppData%.</summary>
    public NotepadWidgetViewModel(TileModel model, IWidgetStateStore stateStore) : base(model)
    {
        _stateStore = stateStore ?? WidgetStateStore.Default;

        // Enforce supported grid bounds
        if (model.SpanX != 8 || (model.SpanY != 4 && model.SpanY != 6 && model.SpanY != 8 && model.SpanY != 10))
        {
            model.SpanX = 8;
            model.SpanY = 4;
        }

        Model.PropertyChanged += OnModelPropertyChanged;

        // Content autosave: writes the widget state file only — never layout.json.
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ContentSaveDelayMs)
        };
        _debounceTimer.Tick += (s, e) =>
        {
            _debounceTimer.Stop();
            SaveContent();
        };

        _layoutTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(LayoutSaveDelayMs)
        };
        _layoutTimer.Tick += (s, e) =>
        {
            _layoutTimer.Stop();
            SaveLayoutIfNeeded();
        };

        LoadSettings(model.SettingsJson);
        _isSettingsLoaded = true;

        // Migration / first run: persist the note once (state file) and, when the layout still
        // carries the inline note, strip it. Both are idempotent and never run per keystroke.
        if (_legacyPayloadPresent || _stateNeedsInitialWrite)
        {
            ScheduleSave();
        }
        if (_legacyPayloadPresent)
        {
            ScheduleLayoutSave();
        }
    }

    #region Property Change Handlers

    partial void OnNoteTextChanged(string value)
    {
        if (!_isSettingsLoaded) return;
        ScheduleSave();
    }

    partial void OnActiveViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsNotesView));
        OnPropertyChanged(nameof(IsTodoView));
        OnPropertyChanged(nameof(IsBulletActiveInNotes));
        OnPropertyChanged(nameof(IsNumberedActiveInNotes));
        OnPropertyChanged(nameof(CurrentTileSelection));
        if (!_isSettingsLoaded) return;
        ScheduleSave();
    }

    partial void OnActiveListTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsBulletActive));
        OnPropertyChanged(nameof(IsNumberedActive));
        OnPropertyChanged(nameof(IsBulletActiveInNotes));
        OnPropertyChanged(nameof(IsNumberedActiveInNotes));
        OnPropertyChanged(nameof(CurrentTileSelection));
    }

    #endregion

    #region Task Item Wiring

    private void WireTask(TodoTaskItem task)
    {
        task.PropertyChanged += OnTaskItemPropertyChanged;
    }

    private void UnwireTask(TodoTaskItem task)
    {
        task.PropertyChanged -= OnTaskItemPropertyChanged;
    }

    private void OnTaskItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyTaskCounts();
        if (_isSettingsLoaded)
        {
            ScheduleSave();
        }
    }

    private void NotifyTaskCounts()
    {
        OnPropertyChanged(nameof(RemainingTasksCount));
        OnPropertyChanged(nameof(CompletedTasksCount));
        OnPropertyChanged(nameof(TotalTasksCount));
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasCompletedTasks));
        OnPropertyChanged(nameof(TasksSummary));
    }

    #endregion

    #region Auto-Save & Settings Serialization

    /// <summary>Debounced autosave: schedules the cheap state-file write.</summary>
    public void ScheduleSave()
    {
        if (!_isSettingsLoaded) return;
        _debounceTimer?.Stop();
        _debounceTimer?.Start();
    }

    private void ScheduleLayoutSave()
    {
        if (!_isSettingsLoaded) return;
        _layoutTimer?.Stop();
        _layoutTimer?.Start();
    }

    /// <summary>
    /// Autosave: writes the note body + to-do list to the widget state file. Deliberately does not
    /// touch layout.json — that is the whole point of the split. Skipped when the serialized
    /// payload is byte-identical to the last write.
    /// </summary>
    public void SaveContent()
    {
        _debounceTimer?.Stop();
        try
        {
            var payload = new NotepadWidgetState().CloneForSave(NoteText, ActiveViewMode, Tasks);
            string json = WidgetSerializer.Serialize(payload);

            _stateNeedsInitialWrite = false;
            if (string.Equals(json, _lastSavedStateJson, StringComparison.Ordinal))
            {
                return;
            }

            _lastSavedStateJson = json;
            _stateStore.Write(WidgetId, Model.Id, json);
        }
        catch
        {
            // Fail-safe
        }
    }

    /// <summary>
    /// Writes the slim layout payload (schema + state pointer) into the tile's SettingsJson and,
    /// only when it actually changed, asks the hub to persist layout.json. Migration and lifecycle
    /// saves call this; the typing path never does.
    /// </summary>
    public void SaveLayoutIfNeeded()
    {
        _layoutTimer?.Stop();
        if (_layoutStubPersisted && !_legacyPayloadPresent)
        {
            return;
        }

        try
        {
            var settings = new NotepadWidgetSettings
            {
                SchemaVersion = NotepadWidgetSettings.CurrentSchemaVersion,
                StateRef = Model.Id
            };

            Model.SettingsJson = WidgetSerializer.Serialize(settings);
            _layoutStubPersisted = true;
            _legacyPayloadPresent = false;
            NotifySettingsChanged();
        }
        catch
        {
            // Fail-safe
        }
    }

    protected override void LoadSettings(string? settingsJson)
    {
        _debounceTimer?.Stop();
        _layoutTimer?.Stop();
        _lastSavedStateJson = null;

        NotepadWidgetSettings? settings = null;
        if (!string.IsNullOrWhiteSpace(settingsJson))
        {
            try
            {
                settings = WidgetSerializer.Deserialize<NotepadWidgetSettings>(settingsJson);
            }
            catch
            {
                settings = null;
            }
        }

        _legacyPayloadPresent = settings?.IsLegacyPayload == true;
        _layoutStubPersisted = settings is { IsLegacyPayload: false };

        // Source of truth: the widget state file. The layout payload is only a migration input.
        NotepadWidgetState? state = TryReadStateFile();
        if (state == null)
        {
            _stateNeedsInitialWrite = true;
            state = _legacyPayloadPresent ? settings!.TryBuildLegacyState() : null;
        }

        state ??= new NotepadWidgetState();
        state.Normalize();

        NoteText = state.NoteText;
        ActiveViewMode = state.ActiveViewMode;

        // Unwire any old tasks
        foreach (var oldTask in Tasks)
        {
            UnwireTask(oldTask);
        }
        Tasks.Clear();

        foreach (var task in state.Tasks)
        {
            WireTask(task);
            Tasks.Add(task);
        }

        NotifyTaskCounts();
    }

    private NotepadWidgetState? TryReadStateFile()
    {
        try
        {
            string? json = _stateStore.Read(WidgetId, Model.Id);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var state = WidgetSerializer.Deserialize<NotepadWidgetState>(json);
            if (state == null)
            {
                return null;
            }

            state.Normalize();
            return state;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lifecycle save: the note mirror plus the slim layout pointer.</summary>
    public override void SaveSettings()
    {
        SaveContent();
        SaveLayoutIfNeeded();
    }

    public override void Pause()
    {
        base.Pause();
        // Both halves are diff-guarded, so flushing on every hub hide is cheap when nothing changed.
        SaveSettings();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TileModel.SpanX) or nameof(TileModel.SpanY))
        {
            OnPropertyChanged(nameof(IsMegaSize));
            OnPropertyChanged(nameof(IsHugeSize));
            OnPropertyChanged(nameof(IsCanvasSize));
            OnPropertyChanged(nameof(IsFullSize));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Model.PropertyChanged -= OnModelPropertyChanged;
            _debounceTimer?.Stop();
            _layoutTimer?.Stop();

            // Never lose in-flight edits: the note mirror + the slim layout pointer.
            SaveContent();
            SaveLayoutIfNeeded();

            foreach (var task in Tasks)
            {
                UnwireTask(task);
            }
        }
        base.Dispose(disposing);
    }

    #endregion

    #region Commands

    [RelayCommand]
    public void SwitchToNotes()
    {
        ActiveViewMode = "Notes";
    }

    [RelayCommand]
    public void SwitchToTodo()
    {
        ActiveViewMode = "Todo";
    }

    [RelayCommand]
    public void ToggleBulletList()
    {
        if (ActiveViewMode != "Notes")
        {
            ActiveViewMode = "Notes";
        }
        ActiveListType = (ActiveListType == "Bullet") ? "None" : "Bullet";
    }

    [RelayCommand]
    public void ToggleNumberedList()
    {
        if (ActiveViewMode != "Notes")
        {
            ActiveViewMode = "Notes";
        }
        ActiveListType = (ActiveListType == "Numbered") ? "None" : "Numbered";
    }

    [RelayCommand]
    public void AddTask()
    {
        if (string.IsNullOrWhiteSpace(NewTaskText))
            return;

        var newTask = new TodoTaskItem(NewTaskText.Trim(), false);
        WireTask(newTask);
        Tasks.Add(newTask);
        NewTaskText = string.Empty;

        NotifyTaskCounts();
        ScheduleSave();
    }

    [RelayCommand]
    public void ToggleTask(TodoTaskItem? task)
    {
        if (task == null) return;
        task.IsCompleted = !task.IsCompleted;
    }

    [RelayCommand]
    public void DeleteTask(TodoTaskItem? task)
    {
        if (task == null) return;
        UnwireTask(task);
        Tasks.Remove(task);

        NotifyTaskCounts();
        ScheduleSave();
    }

    [RelayCommand]
    public void ClearCompleted()
    {
        var completed = Tasks.Where(t => t.IsCompleted).ToList();
        if (completed.Count == 0) return;

        foreach (var task in completed)
        {
            UnwireTask(task);
            Tasks.Remove(task);
        }

        NotifyTaskCounts();
        ScheduleSave();
    }

    #endregion

    #region Smart List Transformation & Enter Key Hook

    /// <summary>
    /// Handles the Enter key within the multi-line text editor.
    /// Provides intelligent auto-continuation for bullet lists and numbered lists.
    /// Exits list mode automatically when Enter is pressed on an empty item line.
    /// </summary>
    public bool HandleEnterKeyPress(ref int caretIndex)
    {
        string text = NoteText ?? string.Empty;
        int caret = Math.Clamp(caretIndex, 0, text.Length);

        // Find the start of the current line
        int lineStart = caret > 0 ? text.LastIndexOf('\n', caret - 1) + 1 : 0;
        string lineBeforeCaret = text.Substring(lineStart, caret - lineStart);

        // Check if line before caret is a bullet item
        var bulletMatch = Regex.Match(lineBeforeCaret, @"^(\s*)([●•\-\*○■▪])\s*(.*)$");
        if (bulletMatch.Success)
        {
            string indent = bulletMatch.Groups[1].Value;
            string symbol = bulletMatch.Groups[2].Value;
            string content = bulletMatch.Groups[3].Value;

            if (string.IsNullOrWhiteSpace(content))
            {
                // If line has indentation, outdent one level (4 spaces) rather than exiting completely
                if (indent.Length >= 4)
                {
                    string newIndent = indent.Substring(4);
                    string newSymbol = newIndent.Length >= 4 ? "○ " : "● ";
                    string newBulletLine = newIndent + newSymbol;
                    int removeStart = lineStart;
                    int removeLength = caret - lineStart;
                    string newText = text.Remove(removeStart, removeLength).Insert(removeStart, newBulletLine);
                    NoteText = newText;
                    caretIndex = removeStart + newBulletLine.Length;
                    return true;
                }
                else
                {
                    // Level-0 bullet -> erase bullet and exit list mode
                    int removeStart = lineStart;
                    int removeLength = caret - lineStart;
                    if (removeStart > 0 && text[removeStart - 1] == '\n')
                    {
                        removeStart--;
                        removeLength++;
                        if (removeStart > 0 && text[removeStart - 1] == '\r')
                        {
                            removeStart--;
                            removeLength++;
                        }
                    }

                    string newText = text.Remove(removeStart, removeLength);
                    NoteText = newText;
                    caretIndex = removeStart;
                    ActiveListType = "None";
                    return true;
                }
            }
            else
            {
                // Auto-continue bullet on next line with matching indent and symbol
                string insert = "\n" + indent + symbol + " ";
                string newText = text.Insert(caret, insert);
                NoteText = newText;
                caretIndex = caret + insert.Length;
                ActiveListType = "Bullet";
                return true;
            }
        }

        // Check if line before caret is a numbered list item
        var numberMatch = Regex.Match(lineBeforeCaret, @"^(\s*)(\d+)\.\s*(.*)$");
        if (numberMatch.Success)
        {
            string indent = numberMatch.Groups[1].Value;
            int number = int.Parse(numberMatch.Groups[2].Value);
            string content = numberMatch.Groups[3].Value;

            if (string.IsNullOrWhiteSpace(content))
            {
                // If line has indentation, outdent one level
                if (indent.Length >= 4)
                {
                    string newIndent = indent.Substring(4);
                    string newNumberLine = newIndent + "1. ";
                    int removeStart = lineStart;
                    int removeLength = caret - lineStart;
                    string newText = text.Remove(removeStart, removeLength).Insert(removeStart, newNumberLine);
                    NoteText = newText;
                    caretIndex = removeStart + newNumberLine.Length;
                    return true;
                }
                else
                {
                    // Empty numbered line -> erase number and exit list mode
                    int removeStart = lineStart;
                    int removeLength = caret - lineStart;
                    if (removeStart > 0 && text[removeStart - 1] == '\n')
                    {
                        removeStart--;
                        removeLength++;
                        if (removeStart > 0 && text[removeStart - 1] == '\r')
                        {
                            removeStart--;
                            removeLength++;
                        }
                    }

                    string newText = text.Remove(removeStart, removeLength);
                    NoteText = newText;
                    caretIndex = removeStart;
                    ActiveListType = "None";
                    return true;
                }
            }
            else
            {
                // Auto-increment number on next line
                string insert = $"\n{indent}{number + 1}. ";
                string newText = text.Insert(caret, insert);
                NoteText = newText;
                caretIndex = caret + insert.Length;
                ActiveListType = "Numbered";
                return true;
            }
        }

        // If list mode is active but current line didn't have bullet prefix yet
        if (ActiveListType == "Bullet")
        {
            string insert = "\n● ";
            string newText = text.Insert(caret, insert);
            NoteText = newText;
            caretIndex = caret + insert.Length;
            return true;
        }

        if (ActiveListType == "Numbered")
        {
            string insert = "\n1. ";
            string newText = text.Insert(caret, insert);
            NoteText = newText;
            caretIndex = caret + insert.Length;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles Tab (indent) and Shift+Tab (outdent) for lists and text.
    /// Supports single-line and multi-line selections.
    /// Updates nested bullet markers (● -> ○ -> ▪) and resets sub-numbers appropriately.
    /// </summary>
    public bool HandleTabKeyPress(ref int caretIndex, ref int selectionLength, bool isShiftTab)
    {
        string text = NoteText ?? string.Empty;
        int selStart = Math.Clamp(caretIndex, 0, text.Length);
        int selLen = Math.Clamp(selectionLength, 0, text.Length - selStart);
        int selEnd = selStart + selLen;

        // Find the start of the first line
        int firstLineStart = selStart > 0 ? text.LastIndexOf('\n', selStart - 1) + 1 : 0;

        // Find the end of the last line
        int lastLineEnd;
        if (selLen > 0 && selEnd > firstLineStart && text[selEnd - 1] == '\n')
        {
            lastLineEnd = selEnd - 1;
            if (lastLineEnd > 0 && text[lastLineEnd - 1] == '\r')
                lastLineEnd--;
        }
        else
        {
            lastLineEnd = text.IndexOf('\n', selEnd);
            if (lastLineEnd == -1) lastLineEnd = text.Length;
        }

        if (lastLineEnd < firstLineStart) lastLineEnd = firstLineStart;

        string rangeText = text.Substring(firstLineStart, lastLineEnd - firstLineStart);
        string[] lines = rangeText.Split('\n');
        var newLines = new List<string>();
        int firstLineDelta = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string rawLine = lines[i];
            bool hasCarriageReturn = rawLine.EndsWith('\r');
            string line = hasCarriageReturn ? rawLine.Substring(0, rawLine.Length - 1) : rawLine;

            string transformedLine;
            int delta;

            if (!isShiftTab)
            {
                // INDENT (Tab)
                var bulletMatch = Regex.Match(line, @"^(\s*)([●•\-\*○■▪])\s*(.*)$");
                if (bulletMatch.Success)
                {
                    string indent = bulletMatch.Groups[1].Value + "    ";
                    string content = bulletMatch.Groups[3].Value;
                    string symbol = indent.Length >= 8 ? "▪ " : "○ ";
                    transformedLine = indent + symbol + content;
                    delta = transformedLine.Length - line.Length;
                }
                else
                {
                    var numMatch = Regex.Match(line, @"^(\s*)(\d+)\.\s*(.*)$");
                    if (numMatch.Success)
                    {
                        string indent = numMatch.Groups[1].Value + "    ";
                        string content = numMatch.Groups[3].Value;
                        transformedLine = indent + "1. " + content;
                        delta = transformedLine.Length - line.Length;
                    }
                    else
                    {
                        transformedLine = "    " + line;
                        delta = 4;
                    }
                }
            }
            else
            {
                // OUTDENT (Shift + Tab)
                var bulletMatch = Regex.Match(line, @"^(\s*)([●•\-\*○■▪])\s*(.*)$");
                if (bulletMatch.Success)
                {
                    string oldIndent = bulletMatch.Groups[1].Value;
                    int spacesToRemove = Math.Min(4, oldIndent.Length);
                    string newIndent = oldIndent.Substring(spacesToRemove);
                    string content = bulletMatch.Groups[3].Value;
                    string symbol = newIndent.Length >= 8 ? "▪ " : (newIndent.Length >= 4 ? "○ " : "● ");
                    transformedLine = newIndent + symbol + content;
                    delta = transformedLine.Length - line.Length;
                }
                else
                {
                    var numMatch = Regex.Match(line, @"^(\s*)(\d+)\.\s*(.*)$");
                    if (numMatch.Success)
                    {
                        string oldIndent = numMatch.Groups[1].Value;
                        int spacesToRemove = Math.Min(4, oldIndent.Length);
                        string newIndent = oldIndent.Substring(spacesToRemove);
                        string content = numMatch.Groups[3].Value;
                        transformedLine = newIndent + numMatch.Groups[2].Value + ". " + content;
                        delta = transformedLine.Length - line.Length;
                    }
                    else
                    {
                        int spacesToRemove = 0;
                        if (line.StartsWith("    ", StringComparison.Ordinal)) spacesToRemove = 4;
                        else if (line.StartsWith('\t')) spacesToRemove = 1;
                        else
                        {
                            while (spacesToRemove < line.Length && line[spacesToRemove] == ' ' && spacesToRemove < 4)
                                spacesToRemove++;
                        }
                        transformedLine = line.Substring(spacesToRemove);
                        delta = -spacesToRemove;
                    }
                }
            }

            if (i == 0) firstLineDelta = delta;
            newLines.Add(hasCarriageReturn ? transformedLine + "\r" : transformedLine);
        }

        string replacementBlock = string.Join("\n", newLines);
        string newFullText = text.Substring(0, firstLineStart) + replacementBlock + text.Substring(lastLineEnd);
        NoteText = newFullText;

        if (selLen == 0)
        {
            caretIndex = Math.Clamp(selStart + firstLineDelta, firstLineStart, firstLineStart + replacementBlock.Length);
            selectionLength = 0;
        }
        else
        {
            caretIndex = firstLineStart;
            selectionLength = replacementBlock.Length;
        }

        return true;
    }

    /// <summary>
    /// Transforms the current line or selected lines to Bullet or Numbered list.
    /// Replaces any existing list prefixes cleanly without appending or duplicating.
    /// Toggles the formatting off if the target format is already active on the line(s).
    /// </summary>
    public void TransformLineList(ref int caretIndex, ref int selectionLength, string targetType)
    {
        string text = NoteText ?? string.Empty;
        int selStart = Math.Clamp(caretIndex, 0, text.Length);
        int selLen = Math.Clamp(selectionLength, 0, text.Length - selStart);
        int selEnd = selStart + selLen;

        // Find the start of the first line
        int firstLineStart = selStart > 0 ? text.LastIndexOf('\n', selStart - 1) + 1 : 0;
        // Find the end of the last line
        int lastLineEnd = text.IndexOf('\n', selEnd);
        if (lastLineEnd == -1) lastLineEnd = text.Length;

        string rangeText = text.Substring(firstLineStart, lastLineEnd - firstLineStart);
        string[] lines = rangeText.Split('\n');

        // Regex for stripping list prefixes: bullets (●, •, -, *, ○, ■, ▪) or numbered items (1., 2., etc.)
        var prefixRegex = ListPrefixRegex();

        // Check if all non-empty lines already have the target format
        bool allAlreadyHaveTarget = true;
        int nonEmptyCount = 0;
        foreach (var line in lines)
        {
            string trimmedLine = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmedLine)) continue;
            nonEmptyCount++;

            if (targetType == "Bullet")
            {
                if (!BulletLineRegex().IsMatch(trimmedLine))
                {
                    allAlreadyHaveTarget = false;
                    break;
                }
            }
            else if (targetType == "Numbered")
            {
                if (!NumberedLineRegex().IsMatch(trimmedLine))
                {
                    allAlreadyHaveTarget = false;
                    break;
                }
            }
        }

        bool shouldRemove = nonEmptyCount > 0 && allAlreadyHaveTarget;
        var newLines = new List<string>();
        int numberCounter = 1;

        for (int i = 0; i < lines.Length; i++)
        {
            string rawLine = lines[i];
            bool hasCarriageReturn = rawLine.EndsWith('\r');
            string line = hasCarriageReturn ? rawLine.Substring(0, rawLine.Length - 1) : rawLine;

            // Strip existing prefix
            string stripped = prefixRegex.Replace(line, "$1");

            if (shouldRemove)
            {
                // Toggle off
                newLines.Add(hasCarriageReturn ? stripped + "\r" : stripped);
            }
            else
            {
                // If single empty line and user clicked list icon, insert prefix
                if (lines.Length == 1 && string.IsNullOrEmpty(line))
                {
                    string prefix = targetType == "Bullet" ? "● " : "1. ";
                    newLines.Add(hasCarriageReturn ? prefix + "\r" : prefix);
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    // Preserve empty lines in multi-line selection
                    newLines.Add(rawLine);
                }
                else
                {
                    // Add new prefix
                    string prefix = targetType == "Bullet" ? "● " : $"{numberCounter++}. ";
                    string transformed = prefix + stripped.TrimStart();
                    newLines.Add(hasCarriageReturn ? transformed + "\r" : transformed);
                }
            }
        }

        string replacementBlock = string.Join("\n", newLines);
        string newFullText = text.Substring(0, firstLineStart) + replacementBlock + text.Substring(lastLineEnd);
        NoteText = newFullText;

        // Position caret after replacement
        if (lines.Length == 1)
        {
            caretIndex = firstLineStart + newLines[0].TrimEnd('\r').Length;
            selectionLength = 0;
        }
        else
        {
            caretIndex = firstLineStart;
            selectionLength = replacementBlock.Length;
        }

        ActiveListType = shouldRemove ? "None" : targetType;
    }

    [GeneratedRegex(@"^(\s*)([●•\-\*○■▪]|\d+\.)\s*")]
    private static partial Regex ListPrefixRegex();

    [GeneratedRegex(@"^\s*[●•\-\*○■▪]\s+")]
    private static partial Regex BulletLineRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+")]
    private static partial Regex NumberedLineRegex();

    #endregion
}
