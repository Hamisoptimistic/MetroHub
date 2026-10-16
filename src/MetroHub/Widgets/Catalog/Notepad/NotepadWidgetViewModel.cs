using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Notepad;

/// <summary>
/// ViewModel for the Notepad & Todo Widget.
/// Manages both free-form rich text notes and interactive tasks with history navigation.
/// </summary>
public sealed partial class NotepadWidgetViewModel : WidgetViewModelBase
{
    public const string WidgetId = "notepad";

    private static readonly char[] WordSeparators = [' ', '\r', '\n', '\t'];

    private readonly IWidgetStateStore _stateStore;
    private string? _lastSavedJson;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotesMode))]
    [NotifyPropertyChangedFor(nameof(IsTodoMode))]
    [NotifyPropertyChangedFor(nameof(IsActiveTodoView))]
    [NotifyPropertyChangedFor(nameof(IsHistoryTodoView))]
    private NotepadMode _currentMode = NotepadMode.Notes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveTodoView))]
    [NotifyPropertyChangedFor(nameof(IsHistoryTodoView))]
    private bool _isHistoryView;

    [ObservableProperty]
    private string _documentXaml = string.Empty;

    [ObservableProperty]
    private string _plainText = string.Empty;

    [ObservableProperty]
    private int _wordCount;

    [ObservableProperty]
    private int _charCount;

    [ObservableProperty]
    private bool _isWordWrapEnabled = true;

    [ObservableProperty]
    private string _saveStatusText = "Saved";

    [ObservableProperty]
    private bool _isSaveStatusVisible;

    [ObservableProperty]
    private string _newTodoText = string.Empty;

    public Action? RequestDocumentSync { get; set; }

    public ObservableCollection<NotepadTodoItem> ActiveTodos { get; } = new();

    public ObservableCollection<NotepadTodoItem> CompletedTodos { get; } = new();

    public bool IsNotesMode => CurrentMode == NotepadMode.Notes;

    public bool IsTodoMode => CurrentMode == NotepadMode.Todo;

    public bool IsActiveTodoView => CurrentMode == NotepadMode.Todo && !IsHistoryView;

    public bool IsHistoryTodoView => CurrentMode == NotepadMode.Todo && IsHistoryView;

    public int CompletedCount => CompletedTodos.Count;

    public override IReadOnlyList<WidgetSize> AllowedSizes =>
    [
        WidgetSize.Huge,     // 8x6 (Default)
        WidgetSize.Canvas,   // 8x8
        WidgetSize.Full      // 8x10
    ];

    public NotepadWidgetViewModel(TileModel model) : this(model, WidgetStateStore.Default)
    {
    }

    public NotepadWidgetViewModel(TileModel model, IWidgetStateStore stateStore) : base(model)
    {
        _stateStore = stateStore ?? WidgetStateStore.Default;

        ActiveTodos.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ActiveTodos));
        CompletedTodos.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CompletedTodos));
            OnPropertyChanged(nameof(CompletedCount));
        };

        // Try reading from the per-widget state file first (%LocalAppData%\MetroHub\config\widgets\notepad\{tileId}.json)
        string? stateJson = _stateStore.Read(WidgetId, model.Id);
        if (!string.IsNullOrWhiteSpace(stateJson))
        {
            LoadSettings(stateJson);
            _lastSavedJson = stateJson;
        }
        else if (!string.IsNullOrWhiteSpace(model.SettingsJson))
        {
            // Backwards compatibility: load legacy settings previously kept inside layout.json
            LoadSettings(model.SettingsJson);
        }
    }

    [RelayCommand]
    public void ToggleTodoMode()
    {
        RequestDocumentSync?.Invoke();
        CurrentMode = CurrentMode == NotepadMode.Notes ? NotepadMode.Todo : NotepadMode.Notes;
        IsHistoryView = false;
        SaveSettings();
    }

    [RelayCommand]
    public void ToggleWordWrap()
    {
        IsWordWrapEnabled = !IsWordWrapEnabled;
        SaveSettings();
    }

    [RelayCommand]
    public void ToggleHistory()
    {
        IsHistoryView = !IsHistoryView;
    }

    [RelayCommand]
    public void AddTodo()
    {
        var text = NewTodoText?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        ActiveTodos.Add(new NotepadTodoItem
        {
            Text = text,
            IsCompleted = false
        });

        NewTodoText = string.Empty;
        SaveSettings();
    }

    [RelayCommand]
    public void ToggleTodoCompletion(NotepadTodoItem item)
    {
        if (item == null) return;

        if (item.IsCompleted)
        {
            // Moving from active to completed
            item.CompletedAt = DateTime.UtcNow;
            ActiveTodos.Remove(item);
            if (!CompletedTodos.Contains(item))
            {
                CompletedTodos.Insert(0, item);
            }
        }
        else
        {
            // Moving from completed back to active
            item.CompletedAt = null;
            CompletedTodos.Remove(item);
            if (!ActiveTodos.Contains(item))
            {
                ActiveTodos.Add(item);
            }
        }

        SaveSettings();
    }

    public void SwitchToNotesMode()
    {
        if (CurrentMode != NotepadMode.Notes)
        {
            RequestDocumentSync?.Invoke();
            CurrentMode = NotepadMode.Notes;
            IsHistoryView = false;
            SaveSettings();
        }
    }

    [RelayCommand]
    public void DeleteTodo(NotepadTodoItem item)
    {
        if (item == null) return;

        ActiveTodos.Remove(item);
        CompletedTodos.Remove(item);
        SaveSettings();
    }

    private CancellationTokenSource? _saveStatusCts;

    [RelayCommand]
    public void Save()
    {
        SaveSettings();
        SaveStatusText = "Saved";
        IsSaveStatusVisible = true;

        _saveStatusCts?.Cancel();
        _saveStatusCts?.Dispose();
        _saveStatusCts = new CancellationTokenSource();
        var token = _saveStatusCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, token).ConfigureAwait(false);
                if (Application.Current != null && !token.IsCancellationRequested)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            IsSaveStatusVisible = false;
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by next save
            }
        }, token);
    }

    public void UpdateTextMetrics(string plainText, string? documentXaml = null)
    {
        PlainText = plainText ?? string.Empty;
        if (documentXaml != null)
        {
            DocumentXaml = documentXaml;
        }

        CharCount = PlainText.Length;
        WordCount = string.IsNullOrWhiteSpace(PlainText)
            ? 0
            : PlainText.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return;

        var settings = WidgetSerializer.Deserialize<NotepadWidgetSettings>(settingsJson);
        if (settings == null) return;

        CurrentMode = Enum.IsDefined(settings.Mode) ? settings.Mode : NotepadMode.Notes;
        IsWordWrapEnabled = settings.IsWordWrapEnabled;
        DocumentXaml = settings.DocumentXaml ?? string.Empty;
        PlainText = settings.PlainText ?? string.Empty;
        UpdateTextMetrics(PlainText);

        ActiveTodos.Clear();
        if (settings.ActiveTodos != null)
        {
            foreach (var item in settings.ActiveTodos)
            {
                ActiveTodos.Add(item);
            }
        }

        CompletedTodos.Clear();
        if (settings.CompletedTodos != null)
        {
            foreach (var item in settings.CompletedTodos)
            {
                CompletedTodos.Add(item);
            }
        }
    }

    public override void SaveSettings()
    {
        RequestDocumentSync?.Invoke();

        var settings = new NotepadWidgetSettings
        {
            Mode = CurrentMode,
            IsWordWrapEnabled = IsWordWrapEnabled,
            DocumentXaml = DocumentXaml,
            PlainText = PlainText,
            ActiveTodos = ActiveTodos.ToList(),
            CompletedTodos = CompletedTodos.ToList()
        };

        string json = WidgetSerializer.Serialize(settings);
        if (string.Equals(json, _lastSavedJson, StringComparison.Ordinal))
        {
            return;
        }

        _lastSavedJson = json;
        _stateStore.Write(WidgetId, Model.Id, json);

        // One-time migration cleanup: if legacy inline settings were stored in layout.json,
        // clear them so layout.json remains slim and does not store redundant note data.
        if (!string.IsNullOrEmpty(Model.SettingsJson))
        {
            Model.SettingsJson = null;
            NotifySettingsChanged();
        }
    }

    private bool _isDisposed;

    public override void Pause()
    {
        base.Pause();
        // Zero-overhead persistence: save whenever MetroHub minimizes or hides to tray
        if (!_isDisposed)
        {
            SaveSettings();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            SaveSettings();
        }

        _isDisposed = true;
        base.Dispose(disposing);

        if (disposing)
        {
            RequestDocumentSync = null;
            _saveStatusCts?.Cancel();
            _saveStatusCts?.Dispose();
            _saveStatusCts = null;

            ActiveTodos.Clear();
            CompletedTodos.Clear();
        }
    }
}
