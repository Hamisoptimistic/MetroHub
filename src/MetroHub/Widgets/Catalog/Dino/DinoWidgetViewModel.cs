using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MetroHub.Core.Models;
using MetroHub.Widgets.Catalog.Dino;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Dino;

/// <summary>
/// High-performance MVVM ViewModel for the Chrome Dino Game widget (Implementation Plan v2).
/// Manages the game engine, procedural audio, settings persistence, and delta-time rendering loop.
/// Subscribes to CompositionTarget.Rendering ONLY during active gameplay (Running / Dying)
/// to strictly maintain 0.0% CPU usage when Idle, Paused, or Game Over.
/// </summary>
public sealed partial class DinoWidgetViewModel : WidgetViewModelBase
{
    private readonly DinoGameEngine _engine;
    private readonly DinoAudioService _audioService;

    private bool _isRenderingHooked;
    private TimeSpan _lastRenderingTime = TimeSpan.Zero;

    private bool _isMuted;
    private bool? _reducedMotion;
    private bool _isHighContrast;

    public DinoGameEngine Engine => _engine;
    public DinoAudioService AudioService => _audioService;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Banner3 // 8x3 only (504x184 px)
    };

    /// <summary>
    /// Notification event fired on every game physics update to trigger visual redraw.
    /// </summary>
    public event Action? FrameTick;

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value))
            {
                _audioService.IsMuted = value;
                SaveSettings();
            }
        }
    }

    public bool? ReducedMotion
    {
        get => _reducedMotion;
        set
        {
            if (SetProperty(ref _reducedMotion, value))
            {
                OnPropertyChanged(nameof(EffectiveReducedMotion));
                SaveSettings();
            }
        }
    }

    public bool EffectiveReducedMotion => _reducedMotion ?? !SystemParameters.ClientAreaAnimation;

    public bool IsHighContrast
    {
        get => _isHighContrast;
        private set => SetProperty(ref _isHighContrast, value);
    }

    public DinoWidgetViewModel(TileModel model) : base(model)
    {
        _audioService = new DinoAudioService();
        _engine = new DinoGameEngine();

        _engine.OnJump += OnEngineJump;
        _engine.OnMilestone += OnEngineMilestone;
        _engine.OnGameOver += OnEngineGameOver;

        _isHighContrast = SystemParameters.HighContrast;
        SystemParameters.StaticPropertyChanged += OnSystemParametersPropertyChanged;

        LoadSettings(model.SettingsJson);
    }

    private void OnEngineJump()
    {
        _audioService.PlayJump();
    }

    private void OnEngineMilestone()
    {
        _audioService.PlayMilestone();
    }

    private void OnEngineGameOver()
    {
        _audioService.PlayGameOver();
        SaveSettings();
    }

    private void OnSystemParametersPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            OnPropertyChanged(nameof(EffectiveReducedMotion));
        }
        else if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            IsHighContrast = SystemParameters.HighContrast;
        }
    }

    public void HandleJumpInput()
    {
        _engine.HandleJumpInput();
        if (_engine.State == DinoGameState.Running)
        {
            HookRendering();
        }
        FrameTick?.Invoke();
    }

    public void RestartGame()
    {
        _engine.RestartGame();
        if (_engine.State == DinoGameState.Running)
        {
            HookRendering();
        }
        FrameTick?.Invoke();
    }

    public void SetDownInput(bool isDown)
    {
        _engine.SetDownInput(isDown);
    }

    public void TogglePause()
    {
        if (_engine.State == DinoGameState.Running)
        {
            _engine.Pause();
            UnhookRendering();
            FrameTick?.Invoke();
        }
        else if (_engine.State == DinoGameState.Paused)
        {
            _engine.Resume();
            HookRendering();
        }
    }

    public void ToggleMute()
    {
        IsMuted = !IsMuted;
    }

    public void SetReducedMotion(bool? setting)
    {
        ReducedMotion = setting;
    }

    public void ResetHighScore()
    {
        _engine.ResetHighScore();
        SaveSettings();
        FrameTick?.Invoke();
    }

    public override void Pause()
    {
        if (_engine.State == DinoGameState.Running)
        {
            _engine.Pause();
        }
        UnhookRendering();
        FrameTick?.Invoke();
    }

    public override void Resume()
    {
        // Do not auto-resume running on window restore without player input to prevent cheap deaths;
        // Keep in Paused state if it was paused.
        FrameTick?.Invoke();
    }

    private void HookRendering()
    {
        if (_isRenderingHooked) return;
        _lastRenderingTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
        _isRenderingHooked = true;
    }

    private void UnhookRendering()
    {
        if (!_isRenderingHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _isRenderingHooked = false;
        _lastRenderingTime = TimeSpan.Zero;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs renderingArgs) return;

        if (_lastRenderingTime == TimeSpan.Zero)
        {
            _lastRenderingTime = renderingArgs.RenderingTime;
            return;
        }

        double dt = (renderingArgs.RenderingTime - _lastRenderingTime).TotalSeconds;
        _lastRenderingTime = renderingArgs.RenderingTime;

        if (dt <= 0.0) return;
        if (dt > 0.05) dt = 0.05; // Clamp delta time to 50ms (20fps floor) to prevent collision tunnel on frame drop

        _engine.Update(dt);
        FrameTick?.Invoke();

        if (_engine.State != DinoGameState.Running && _engine.State != DinoGameState.Dying)
        {
            UnhookRendering();
        }
    }

    protected override void LoadSettings(string? settingsJson)
    {
        var settings = WidgetSerializer.Deserialize<DinoWidgetSettings>(settingsJson);
        if (settings != null)
        {
            _engine.UpdateHighScore(settings.HighScore);
            _isMuted = settings.IsMuted;
            _audioService.IsMuted = settings.IsMuted;
            _reducedMotion = settings.ReducedMotion;
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(ReducedMotion));
            OnPropertyChanged(nameof(EffectiveReducedMotion));
        }
    }

    public override void SaveSettings()
    {
        var settings = new DinoWidgetSettings
        {
            HighScore = _engine.HighScore,
            IsMuted = _isMuted,
            ReducedMotion = _reducedMotion
        };

        Model.SettingsJson = WidgetSerializer.Serialize(settings);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UnhookRendering();
            SystemParameters.StaticPropertyChanged -= OnSystemParametersPropertyChanged;

            _engine.OnJump -= OnEngineJump;
            _engine.OnMilestone -= OnEngineMilestone;
            _engine.OnGameOver -= OnEngineGameOver;

            _audioService.Dispose();
        }

        base.Dispose(disposing);
    }
}
