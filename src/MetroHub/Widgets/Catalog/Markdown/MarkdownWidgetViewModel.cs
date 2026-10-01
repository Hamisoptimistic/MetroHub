using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Serialization;
using Microsoft.Win32;

namespace MetroHub.Widgets.Catalog.Markdown;

/// <summary>
/// ViewModel for the Markdown widget: multi-tab document management, Write/Preview mode toggling,
/// debounced rendering, and file operations.
/// <para>
/// Persistence is split three ways:
/// <list type="bullet">
/// <item>the user's own <c>.md</c> file — written by Save/Save As and by the (mtime-guarded)
/// write-back of every dirty file-backed tab;</item>
/// <item>the widget state file (<c>config\widgets\markdown\{tileId}.json</c>) — the autosave
/// mirror that holds unsaved edits, written on an idle debounce;</item>
/// <item><c>layout.json</c> — only ever a slim schema + state pointer, written when that pointer
/// first lands, never while typing.</item>
/// </list>
/// That split is what keeps a keystroke pause from re-serializing and fsync-ing the whole hub layout.
/// </para>
/// </summary>
public sealed partial class MarkdownWidgetViewModel : WidgetViewModelBase
{
    /// <summary>Registry id of this widget (state-file directory + tile target path).</summary>
    public const string WidgetId = "markdown";

    /// <summary>Hard cap on open documents (single source of truth: <see cref="MarkdownWidgetState.MaxTabs"/>).</summary>
    public const int MaxTabs = MarkdownWidgetState.MaxTabs;

    /// <summary>Idle delay before an autosave writes dirty documents + the widget state file.</summary>
    private const int ContentSaveDelayMs = 1500;

    /// <summary>Idle delay before a layout-pointer change reaches layout.json.</summary>
    private const int LayoutSaveDelayMs = 400;

    private readonly IWidgetStateStore _stateStore;
    private DispatcherTimer? _contentTimer;
    private DispatcherTimer? _layoutTimer;
    private DispatcherTimer? _renderTimer;
    private DispatcherTimer? _statusTimer;
    private bool _isSettingsLoaded;
    private bool _renderStale = true;
    private bool _idleRenderPosted;
    private bool _isVisible;
    private bool _isDisposed;
    private string? _lastRenderKey;         // source+path of the last successful render (skip identical)
    private string? _lastSavedStateJson;    // state-file payload from the last write (skip unchanged)
    private bool _layoutStubPersisted;      // SettingsJson already carries the slim schema-2 pointer
    private bool _legacyPayloadPresent;     // layout.json still holds inline document text → rewrite once

    private static readonly Lazy<MdXaml.Markdown> _engine =
        new(() => new MdXaml.Markdown(), isThreadSafe: false);

    /// <summary>
    /// Raised whenever the text under the single shared editor is replaced (tab switch, file open).
    /// The view uses it to drop the editor's undo history, which would otherwise let Ctrl+Z paste
    /// one document's text into another.
    /// </summary>
    public event EventHandler? ActiveDocumentReplaced;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Mega,   // 8x4
        WidgetSize.Huge,   // 8x6
        WidgetSize.Canvas, // 8x8
        WidgetSize.Full    // 8x10
    };

    public ObservableCollection<DocumentTabItem> Tabs { get; } = new();

    [ObservableProperty]
    private DocumentTabItem? _currentTab;

    [ObservableProperty]
    private string _activeTab = "Write"; // "Write" or "Read" (Preview)

    [ObservableProperty]
    private FlowDocument? _renderedDocument;

    [ObservableProperty]
    private bool _hasRendered;

    [ObservableProperty]
    private bool _isPreviewBlocked;

    [ObservableProperty]
    private string _blockedReason = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    partial void OnStatusMessageChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (_statusTimer == null)
        {
            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                StatusMessage = string.Empty;
            };
        }
        else
        {
            _statusTimer.Stop();
        }

        _statusTimer.Start();
    }

    private Style? _documentStyle;
    /// <summary>Dark document style from the view's resources; applied to the engine once.</summary>
    public Style? DocumentStyle
    {
        get => _documentStyle;
        set
        {
            if (_documentStyle != value)
            {
                _documentStyle = value;
                _lastRenderKey = null;
                if (IsReadTab && _isSettingsLoaded && HasRendered)
                {
                    RenderNow();
                }
            }
        }
    }

    public bool IsWriteTab => string.Equals(ActiveTab, "Write", StringComparison.OrdinalIgnoreCase);

    public bool IsReadTab => !IsWriteTab;

    public bool CanAddTab => Tabs.Count < MaxTabs;

    /// <summary>True while any tab holds text that has not reached its own file yet.</summary>
    public bool HasUnsavedDocuments => Tabs.Any(t => t.IsDirty);

    public string? SourceFilePath => CurrentTab?.SourceFilePath;

    public Wpf.Ui.Controls.SymbolRegular ModeToggleSymbol =>
        IsReadTab ? Wpf.Ui.Controls.SymbolRegular.Edit24 : Wpf.Ui.Controls.SymbolRegular.BookOpen24;

    public string ModeToggleToolTip =>
        IsReadTab ? "Edit markdown (Ctrl+E)" : "Preview document (Ctrl+E)";

    /// <summary>
    /// Two-way proxy to the currently active tab's text content.
    /// </summary>
    public string MarkdownText
    {
        get => CurrentTab?.Text ?? string.Empty;
        set
        {
            if (CurrentTab != null && CurrentTab.Text != value)
            {
                CurrentTab.Text = value; // marks this tab dirty
                OnPropertyChanged(nameof(MarkdownText));
                ScheduleContentSave();
                ScheduleRender();
            }
        }
    }

    public bool IsMegaSize => Model.SpanX == 8 && Model.SpanY == 4;
    public bool IsHugeSize => Model.SpanX == 8 && Model.SpanY == 6;
    public bool IsCanvasSize => Model.SpanX == 8 && Model.SpanY == 8;
    public bool IsFullSize => Model.SpanX == 8 && Model.SpanY == 10;

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TileModel.SpanX) or nameof(TileModel.SpanY))
        {
            OnPropertyChanged(nameof(IsMegaSize));
            OnPropertyChanged(nameof(IsHugeSize));
            OnPropertyChanged(nameof(IsCanvasSize));
            OnPropertyChanged(nameof(IsFullSize));
        }
    }

    public MarkdownWidgetViewModel(TileModel model) : this(model, WidgetStateStore.Default)
    {
    }

    /// <summary>Test seam: inject a state store rooted somewhere other than %LocalAppData%.</summary>
    public MarkdownWidgetViewModel(TileModel model, IWidgetStateStore stateStore) : base(model)
    {
        _stateStore = stateStore ?? WidgetStateStore.Default;

        if (model.SpanX != 8 || (model.SpanY != 4 && model.SpanY != 6 && model.SpanY != 8 && model.SpanY != 10))
        {
            model.SpanX = 8;
            model.SpanY = 4;
        }

        Model.PropertyChanged += OnModelPropertyChanged;

        _contentTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ContentSaveDelayMs)
        };
        _contentTimer.Tick += (s, e) =>
        {
            _contentTimer.Stop();
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

        _renderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _renderTimer.Tick += (s, e) =>
        {
            _renderTimer.Stop();
            if (IsReadTab)
            {
                RenderNow();
            }
            else
            {
                _renderStale = true;
            }
        };

        LoadSettings(model.SettingsJson);
        _isSettingsLoaded = true;

        // Migration / first run: persist the loaded document once (state file) and, when the
        // layout still carries inline text, strip it. Both are idempotent and never run per keystroke.
        if (_legacyPayloadPresent)
        {
            ScheduleLayoutSave();
        }
        if (_legacyPayloadPresent || _stateNeedsInitialWrite)
        {
            ScheduleContentSave();
        }
    }

    /// <summary>Set by load when no state file existed yet (legacy payload or brand-new tile).</summary>
    private bool _stateNeedsInitialWrite;

    partial void OnCurrentTabChanged(DocumentTabItem? oldValue, DocumentTabItem? newValue)
    {
        foreach (var tab in Tabs)
        {
            tab.IsActive = ReferenceEquals(tab, newValue);
        }

        OnPropertyChanged(nameof(MarkdownText));
        OnPropertyChanged(nameof(SourceFilePath));
        _renderStale = true;
        _lastRenderKey = null;

        if (IsReadTab && _isSettingsLoaded)
        {
            RenderNow();
        }
        ScheduleContentSave();
        ActiveDocumentReplaced?.Invoke(this, EventArgs.Empty);
    }

    partial void OnActiveTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsWriteTab));
        OnPropertyChanged(nameof(IsReadTab));
        OnPropertyChanged(nameof(ModeToggleSymbol));
        OnPropertyChanged(nameof(ModeToggleToolTip));

        if (!_isSettingsLoaded) return;
        if (IsReadTab && (_renderStale || !HasRendered))
        {
            RenderNow();
        }
        ScheduleContentSave();
    }

    [RelayCommand]
    public void ToggleMode()
    {
        ActiveTab = IsWriteTab ? "Read" : "Write";
    }

    [RelayCommand]
    public void NewTab()
    {
        if (Tabs.Count >= MaxTabs)
        {
            StatusMessage = $"Maximum {MaxTabs} tabs reached.";
            return;
        }

        int nextNumber = 1;
        while (Tabs.Any(t => string.Equals(t.Title, $"Untitled {nextNumber}", StringComparison.OrdinalIgnoreCase)))
        {
            nextNumber++;
        }

        var newTab = new DocumentTabItem($"Untitled {nextNumber}");
        Tabs.Add(newTab);
        CurrentTab = newTab;
        OnPropertyChanged(nameof(CanAddTab));
        ScheduleContentSave();
    }

    [RelayCommand]
    public void CloseTab(DocumentTabItem? tabToClose)
    {
        tabToClose ??= CurrentTab;
        if (tabToClose == null || !Tabs.Contains(tabToClose)) return;

        // Last chance for the tab's file: never drop unsaved edits on the floor.
        if (!TryReleaseTab(tabToClose)) return;

        int index = Tabs.IndexOf(tabToClose);
        Tabs.Remove(tabToClose);

        if (Tabs.Count == 0)
        {
            var freshTab = new DocumentTabItem("Notes", MarkdownWidgetState.DefaultNoteText);
            Tabs.Add(freshTab);
            CurrentTab = freshTab;
        }
        else if (ReferenceEquals(CurrentTab, tabToClose))
        {
            CurrentTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        OnPropertyChanged(nameof(CanAddTab));
        ScheduleContentSave();
        ScheduleRender();
    }

    /// <summary>Ctrl+W: closes the active document (a KeyBinding cannot bind a CommandParameter).</summary>
    [RelayCommand]
    public void CloseCurrentTab() => CloseTab(CurrentTab);

    [RelayCommand]
    public void CloseOtherTabs(DocumentTabItem? tabToKeep)
    {
        tabToKeep ??= CurrentTab;
        if (tabToKeep == null || !Tabs.Contains(tabToKeep)) return;

        var toRemove = Tabs.Where(t => t != tabToKeep).ToList();
        foreach (var t in toRemove)
        {
            // One refused tab (locked file) leaves the rest of the strip intact.
            if (TryReleaseTab(t))
            {
                Tabs.Remove(t);
            }
        }

        CurrentTab = tabToKeep;
        OnPropertyChanged(nameof(CanAddTab));
        ScheduleContentSave();
        ScheduleRender();
    }

    [RelayCommand]
    public void SelectTab(DocumentTabItem? tab)
    {
        if (tab != null && Tabs.Contains(tab))
        {
            CurrentTab = tab;
        }
    }

    [RelayCommand]
    public void Save()
    {
        if (CurrentTab == null) return;
        if (string.IsNullOrWhiteSpace(CurrentTab.SourceFilePath))
        {
            SaveAs();
            return;
        }

        WriteBackFile(CurrentTab);
        SaveContent();
    }

    [RelayCommand]
    public void SaveAs()
    {
        if (CurrentTab == null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".md",
            FileName = CurrentTab.DisplayTitle.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? CurrentTab.DisplayTitle
                : $"{CurrentTab.DisplayTitle}.md"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, CurrentTab.Text ?? string.Empty);
            CurrentTab.SourceFilePath = dialog.FileName;
            CurrentTab.Title = Path.GetFileName(dialog.FileName);
            CurrentTab.SourceFileWriteUtc = File.GetLastWriteTimeUtc(dialog.FileName).Ticks;
            CurrentTab.IsDirty = false;
            StatusMessage = $"Saved to {Path.GetFileName(dialog.FileName)}";
            OnPropertyChanged(nameof(SourceFilePath));
            SaveContent();
            MarkdownLog.Info($"Saved as '{dialog.FileName}'.");
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to save file.";
            MarkdownLog.Warn($"SaveAs failed '{dialog.FileName}': {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenMarkdownFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(dialog.FileName);
            string title = Path.GetFileName(dialog.FileName);
            long writeUtc = File.GetLastWriteTimeUtc(dialog.FileName).Ticks;

            DocumentTabItem targetTab;
            // If current tab is empty and untitled, load into it; otherwise open a new tab if under cap.
            if (CurrentTab != null && string.IsNullOrEmpty(CurrentTab.SourceFilePath) && string.IsNullOrWhiteSpace(CurrentTab.Text))
            {
                targetTab = CurrentTab;
                targetTab.Title = title;
                targetTab.Text = text;
                targetTab.SourceFilePath = dialog.FileName;
                targetTab.SourceFileWriteUtc = writeUtc;
                targetTab.IsDirty = false;
            }
            else if (Tabs.Count < MaxTabs)
            {
                targetTab = new DocumentTabItem(title, text, dialog.FileName)
                {
                    SourceFileWriteUtc = writeUtc,
                    IsDirty = false
                };
                Tabs.Add(targetTab);
                OnPropertyChanged(nameof(CanAddTab));
            }
            else
            {
                // At the cap there is nowhere safe to put this document: every open tab may hold
                // unsaved work. Refuse instead of overwriting the active document.
                StatusMessage = $"Maximum {MaxTabs} tabs reached — close a tab before opening {title}.";
                MarkdownLog.Info($"Open refused at tab cap ({Tabs.Count} tabs): '{dialog.FileName}'.");
                return;
            }

            CurrentTab = targetTab;
            _renderStale = true;
            ActiveTab = "Read"; // Preview newly opened document
            StatusMessage = $"Opened {title}";
            SaveContent();
            // The editor's text was replaced even when CurrentTab did not change (empty-tab reuse).
            ActiveDocumentReplaced?.Invoke(this, EventArgs.Empty);
            MarkdownLog.Info($"Opened '{dialog.FileName}' ({text.Length} chars).");
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't open that file.";
            MarkdownLog.Warn($"Open failed '{dialog.FileName}': {ex.Message}");
        }
    }

    [RelayCommand]
    public void CopyMarkdown()
    {
        if (CurrentTab == null) return;
        try
        {
            Clipboard.SetText(CurrentTab.Text ?? string.Empty);
            StatusMessage = "Markdown copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Clipboard copy failed.";
            MarkdownLog.Warn($"Clipboard copy failed: {ex.Message}");
        }
    }

    [RelayCommand]
    public void RevealInExplorer()
    {
        if (CurrentTab == null || string.IsNullOrWhiteSpace(CurrentTab.SourceFilePath) || !File.Exists(CurrentTab.SourceFilePath))
        {
            StatusMessage = "Current tab is not saved to disk.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{CurrentTab.SourceFilePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not open File Explorer.";
            MarkdownLog.Warn($"RevealInExplorer failed: {ex.Message}");
        }
    }

    /// <summary>Called from the view's Loaded handler: posts the deferred first render.</summary>
    public void OnViewLoaded()
    {
        if (_idleRenderPosted || HasRendered)
        {
            return;
        }
        _idleRenderPosted = true;
        Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            _idleRenderPosted = false;
            if (_isVisible && IsReadTab && (_renderStale || !HasRendered))
            {
                RenderNow();
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Called from the view's IsVisibleChanged: invisible tiles never pay for render.</summary>
    public void OnVisibilityChanged(bool isVisible)
    {
        _isVisible = isVisible;
        if (isVisible && !_idleRenderPosted && !HasRendered)
        {
            OnViewLoaded();
        }
    }

    #region Persistence

    private void ScheduleContentSave()
    {
        if (!_isSettingsLoaded) return;
        _contentTimer?.Stop();
        _contentTimer?.Start();
    }

    private void ScheduleLayoutSave()
    {
        if (!_isSettingsLoaded) return;
        _layoutTimer?.Stop();
        _layoutTimer?.Start();
    }

    private void ScheduleRender()
    {
        _renderTimer?.Stop();
        _renderTimer?.Start();
    }

    /// <summary>
    /// Autosave (idle-debounced): writes every dirty file-backed document back to its own file and
    /// refreshes the widget state mirror. Deliberately does not touch layout.json — that is the
    /// whole point of the split.
    /// </summary>
    public void SaveContent()
    {
        _contentTimer?.Stop();
        if (Tabs.Count == 0) return;

        try
        {
            FlushDirtyTabs();

            int activeIndex = CurrentTab != null ? Math.Max(0, Tabs.IndexOf(CurrentTab)) : 0;
            var payload = new MarkdownWidgetState { Tabs = Tabs.ToList() }
                .CloneForSave(activeIndex, ActiveTab ?? "Write");
            string json = WidgetSerializer.Serialize(payload);

            _stateNeedsInitialWrite = false;
            if (string.Equals(json, _lastSavedStateJson, StringComparison.Ordinal))
            {
                return;
            }

            _lastSavedStateJson = json;
            _stateStore.Write(WidgetId, Model.Id, json);
        }
        catch (Exception ex)
        {
            MarkdownLog.Warn($"State save failed: {ex.Message}");
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
            var settings = new MarkdownWidgetSettings
            {
                SchemaVersion = MarkdownWidgetSettings.CurrentSchemaVersion,
                StateRef = Model.Id
            };
            string json = WidgetSerializer.Serialize(settings);
            Model.SettingsJson = json;
            _layoutStubPersisted = true;
            _legacyPayloadPresent = false;
            NotifySettingsChanged();
        }
        catch (Exception ex)
        {
            MarkdownLog.Warn($"Layout save failed: {ex.Message}");
        }
    }

    /// <summary>Writes every dirty file-backed tab back to its own document. Returns the write count.</summary>
    public int FlushDirtyTabs()
    {
        int written = 0;
        foreach (var tab in Tabs.ToList())
        {
            if (tab.IsDirty && !string.IsNullOrWhiteSpace(tab.SourceFilePath) && WriteBackFile(tab))
            {
                written++;
            }
        }
        return written;
    }

    /// <summary>
    /// Gate before a tab leaves the collection: a file-backed document with unsaved edits is
    /// written first, and the close is refused (tab kept, status shown) when that write fails.
    /// </summary>
    private bool TryReleaseTab(DocumentTabItem tab)
    {
        if (!tab.IsDirty || string.IsNullOrWhiteSpace(tab.SourceFilePath))
        {
            return true;
        }
        if (WriteBackFile(tab))
        {
            return true;
        }

        StatusMessage = $"Couldn't save {SafeFileName(tab.SourceFilePath)} — tab kept.";
        return false;
    }

    /// <summary>Rewrites one tab's document. Returns whether the file now matches the tab.</summary>
    private bool WriteBackFile(DocumentTabItem tab)
    {
        if (string.IsNullOrWhiteSpace(tab.SourceFilePath)) return false;
        try
        {
            string path = tab.SourceFilePath;
            var info = new FileInfo(path);
            if (info.Exists && info.LastWriteTimeUtc.Ticks != tab.SourceFileWriteUtc && tab.SourceFileWriteUtc != 0)
            {
                // Someone else edited the file: keep their version, keep ours in the widget.
                tab.SourceFileWriteUtc = info.LastWriteTimeUtc.Ticks;
                tab.IsDirty = false;
                StatusMessage = "File changed on disk — kept their version, edits stay in widget.";
                MarkdownLog.Warn($"Write-back skipped (external edit): '{path}'.");
                return false;
            }

            File.WriteAllText(path, tab.Text ?? string.Empty);
            tab.IsDirty = false;
            tab.SourceFileWriteUtc = File.GetLastWriteTimeUtc(path).Ticks;
            StatusMessage = "Saved.";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "Note saved in widget, but file is locked.";
            MarkdownLog.Warn($"File write-back failed '{tab.SourceFilePath}': {ex.Message}");
            return false;
        }
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return "document";
        }
    }

    protected override void LoadSettings(string? settingsJson)
    {
        Tabs.Clear();
        _lastSavedStateJson = null;
        _stateNeedsInitialWrite = false;

        MarkdownWidgetSettings? settings = null;
        if (!string.IsNullOrWhiteSpace(settingsJson))
        {
            try
            {
                settings = WidgetSerializer.Deserialize<MarkdownWidgetSettings>(settingsJson);
            }
            catch
            {
                settings = null;
            }
        }

        _legacyPayloadPresent = settings?.IsLegacyPayload == true;
        _layoutStubPersisted = settings is { IsLegacyPayload: false };

        // Source of truth: the widget state file. The layout payload is only a migration input.
        MarkdownWidgetState? state = TryReadStateFile();
        if (state == null)
        {
            _stateNeedsInitialWrite = true;
            state = _legacyPayloadPresent ? settings!.TryBuildLegacyState() : null;
        }

        state ??= new MarkdownWidgetState();
        state.Normalize();

        foreach (var tab in state.Tabs)
        {
            tab.IsDirty = false;
            Tabs.Add(tab);
        }

        CurrentTab = state.ActiveTabIndex >= 0 && state.ActiveTabIndex < Tabs.Count
            ? Tabs[state.ActiveTabIndex]
            : Tabs.FirstOrDefault();

        ActiveTab = string.Equals(state.ActiveMode, "Read", StringComparison.OrdinalIgnoreCase)
            ? "Read"
            : "Write";
    }

    private MarkdownWidgetState? TryReadStateFile()
    {
        try
        {
            string? json = _stateStore.Read(WidgetId, Model.Id);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var state = WidgetSerializer.Deserialize<MarkdownWidgetState>(json);
            if (state == null)
            {
                MarkdownLog.Warn($"State file unreadable for tile {Model.Id} — falling back.");
                return null;
            }

            state.Normalize();
            return state;
        }
        catch (Exception ex)
        {
            MarkdownLog.Warn($"State load failed for tile {Model.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Lifecycle save: everything (documents + mirror + slim layout pointer).</summary>
    public override void SaveSettings()
    {
        _contentTimer?.Stop();
        _layoutTimer?.Stop();
        SaveContent();
        SaveLayoutIfNeeded();
    }

    public override void Pause()
    {
        base.Pause();
        // Both halves are diff-guarded, so this is cheap when nothing changed.
        SaveSettings();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            _isDisposed = true;
            Model.PropertyChanged -= OnModelPropertyChanged;
            _contentTimer?.Stop();
            _layoutTimer?.Stop();
            _renderTimer?.Stop();
            _statusTimer?.Stop();

            // Never lose in-flight edits: dirty documents + the state mirror.
            SaveContent();
            SaveLayoutIfNeeded();
            RenderedDocument = null;
        }
        base.Dispose(disposing);
    }

    #endregion

    private void RenderNow()
    {
        string text = MarkdownText ?? string.Empty;
        if (!MarkdownRenderGate.ShouldRender(text))
        {
            RenderedDocument = null;
            HasRendered = false;
            IsPreviewBlocked = true;
            BlockedReason = $"Document too long for preview ({text.Length / 1000}K chars, limit {MarkdownRenderGate.MaxRenderChars / 1000}K). The Write tab still works.";
            _renderStale = false;
            _lastRenderKey = null;
            MarkdownLog.Warn($"Preview refused: {text.Length} chars.");
            return;
        }

        string renderKey = (SourceFilePath ?? string.Empty) + "\u0001" + text;
        if (string.Equals(renderKey, _lastRenderKey, StringComparison.Ordinal) &&
            HasRendered && !IsPreviewBlocked)
        {
            _renderStale = false;
            return;
        }

        try
        {
            string? baseDir = SourceFilePath != null ? Path.GetDirectoryName(SourceFilePath) : null;
            string sanitized = MarkdownRenderGate.Sanitize(text, baseDir);
            if (DocumentStyle != null)
            {
                _engine.Value.DocumentStyle = DocumentStyle;
            }
            var sw = Stopwatch.StartNew();
            int editors = 0, taskLists = 0, ballots = 0, chips = 0;
            FlowDocument doc = MarkdownQuarantine.RenderSafe(rendered =>
            {
                FlowDocument sub = _engine.Value.Transform(rendered);
                editors += MarkdownCodeBlocks.ReplaceEditors(sub);
                taskLists += MarkdownTaskLists.HideMarkers(sub);
                ballots += MarkdownTaskLists.NormalizeBallotFont(sub);
                chips += MarkdownInlineCode.ReplaceCodeSpans(sub);
                return sub;
            }, DocumentStyle, sanitized, out int quarantined);
            sw.Stop();
            MarkdownLog.Info($"Render: {text.Length} chars in {sw.ElapsedMilliseconds} ms ({editors} code editors plainified, {taskLists} task-list bullets hidden, {ballots} ballots unified, {chips} inline-code chips, {quarantined} quarantined).");
            if (quarantined > 0)
            {
                MarkdownLog.Warn($"Quarantined {quarantined} failing section(s) in {text.Length}-char doc — partial preview shown.");
            }

            RenderedDocument = null;
            RenderedDocument = doc;
            HasRendered = true;
            IsPreviewBlocked = false;
            BlockedReason = string.Empty;
            _lastRenderKey = renderKey;
        }
        catch (Exception ex)
        {
            RenderedDocument = null;
            HasRendered = false;
            IsPreviewBlocked = true;
            BlockedReason = "Preview failed for this document. The Write tab still works.";
            _lastRenderKey = null;
            MarkdownLog.Warn($"Render failed: {ex.Message}");
        }
        finally
        {
            _renderStale = false;
        }
    }
}
