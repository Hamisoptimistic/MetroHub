using System;
using System.Collections.Generic;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Widgets.Serialization;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Catalog.Game2048;

/// <summary>
/// High-performance ViewModel for the 2048 game widget.
/// Manages game loop, scoring, high scores, persistence, and input routing.
/// </summary>
public sealed partial class Game2048WidgetViewModel : WidgetViewModelBase, IWidgetContextMenuProvider
{
    public const string WidgetId = "game_2048";

    private readonly Game2048Engine _engine;

    [ObservableProperty]
    private int _score;

    [ObservableProperty]
    private int _bestScore;

    [ObservableProperty]
    private bool _isGameOver;

    [ObservableProperty]
    private bool _hasWon;

    [ObservableProperty]
    private bool _keepPlaying;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    public Game2048Engine Engine => _engine;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Canvas // 8x8 (508x508 px)
    };

    public event Action? BoardChanged;

    public Game2048WidgetViewModel(TileModel model) : base(model)
    {
        _engine = new Game2048Engine();

        LoadSettings(model.SettingsJson);
        SyncFromEngine();
    }

    public void Move(MoveDirection direction)
    {
        if (_engine.Move(direction))
        {
            SyncFromEngine();
            BoardChanged?.Invoke();
            SaveSettings();
        }
    }

    [RelayCommand]
    public void RestartGame()
    {
        _engine.Reset();
        SyncFromEngine();
        BoardChanged?.Invoke();
        SaveSettings();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo()
    {
        if (_engine.Undo())
        {
            SyncFromEngine();
            BoardChanged?.Invoke();
            SaveSettings();
        }
    }

    [RelayCommand]
    public void ContinueAfterWin()
    {
        _engine.KeepPlaying = true;
        KeepPlaying = true;
        HasWon = false;
        SaveSettings();
    }

    [RelayCommand]
    public void ResetHighScore()
    {
        _engine.BestScore = 0;
        BestScore = 0;
        SaveSettings();
    }

    private void SyncFromEngine()
    {
        Score = _engine.Score;
        BestScore = _engine.BestScore;
        IsGameOver = _engine.IsGameOver;
        HasWon = _engine.HasWon && !_engine.KeepPlaying;
        KeepPlaying = _engine.KeepPlaying;
        CanUndo = _engine.CanUndo;
    }

    protected override void LoadSettings(string? settingsJson)
    {
        if (_engine == null) return; // Might be invoked by base ctor before field initializer
        var settings = WidgetSerializer.Deserialize<Game2048WidgetSettings>(settingsJson);
        if (settings != null)
        {
            _engine.LoadState(settings.SavedGrid, settings.CurrentScore, settings.HighScore);
        }
    }

    public override void SaveSettings()
    {
        if (_engine == null) return;
        var settings = new Game2048WidgetSettings
        {
            HighScore = _engine.BestScore,
            CurrentScore = _engine.Score,
            SavedGrid = _engine.ExportGrid()
        };

        Model.SettingsJson = WidgetSerializer.Serialize(settings);
    }

    public IEnumerable<Control> GetContextMenuItems()
    {
        var undoItem = WidgetMenu.Item("Undo", SymbolRegular.ArrowUndo24, Undo);
        undoItem.IsEnabled = CanUndo;
        yield return undoItem;
        yield return WidgetMenu.Item("New Game", SymbolRegular.ArrowReset24, RestartGame);
        yield return WidgetMenu.Item("Reset Best Score", SymbolRegular.Delete24, ResetHighScore);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SaveSettings();
        }
        base.Dispose(disposing);
    }
}
