using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Core.Radio;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// Lead architect ViewModel for the Focus Radio & Ambient Sounds widget.
/// Enforces seamless 4-column border-to-border layout, singleton audio synchronization,
/// and responsive category browsing with WinRT MediaPlayer.
/// </summary>
public sealed partial class RadioWidgetViewModel : WidgetViewModelBase
{
    private readonly IRadioAudioService _audioService;
    private readonly RadioCatalogService _catalogService;
    private bool _isSyncingVolume;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Huge // 8x6 (504x376px)
    };

    [ObservableProperty]
    private string _selectedCategoryId = "ambient";

    [ObservableProperty]
    private RadioStation? _currentStation;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isBuffering;

    [ObservableProperty]
    private double _volume = 50.0;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private string _currentStationTitle = "Select a station to focus";

    [ObservableProperty]
    private string _currentStationBadge = string.Empty;

    [ObservableProperty]
    private string _playbackStatusText = "READY";

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public ObservableCollection<RadioStationItemViewModel> VisibleStations { get; } = new();

    public RadioWidgetViewModel(TileModel model) 
        : this(model, RadioAudioService.Instance, RadioCatalogService.Instance)
    {
    }

    public RadioWidgetViewModel(TileModel model, IRadioAudioService audioService, RadioCatalogService catalogService) 
        : base(model)
    {
        _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));

        // Subscribe to singleton audio service events
        _audioService.CurrentStationChanged += OnAudioCurrentStationChanged;
        _audioService.PlaybackStateChanged += OnAudioPlaybackStateChanged;
        _audioService.BufferingStateChanged += OnAudioBufferingStateChanged;
        _audioService.VolumeChanged += OnAudioVolumeChanged;
        _audioService.MuteStateChanged += OnAudioMuteStateChanged;
        _audioService.ErrorOccurred += OnAudioErrorOccurred;

        // Initialize state from audio service
        SyncFromAudioService();
    }

    public override void Initialize(TileModel model)
    {
        base.Initialize(model);
        LoadCategoryStations(SelectedCategoryId);
    }

    protected override void LoadSettings(string? settingsJson)
    {
        base.LoadSettings(settingsJson);
        if (string.IsNullOrWhiteSpace(settingsJson)) return;

        try
        {
            var settings = WidgetSerializer.Deserialize<RadioWidgetSettings>(settingsJson);
            if (settings != null)
            {
                if (!string.IsNullOrWhiteSpace(settings.LastSelectedCategory))
                {
                    SelectedCategoryId = settings.LastSelectedCategory;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioWidgetViewModel] Failed to load settings: {ex.Message}");
        }
    }

    public override void SaveSettings()
    {
        base.SaveSettings();
        try
        {
            var settings = new RadioWidgetSettings
            {
                LastSelectedCategory = SelectedCategoryId,
                LastStationId = _audioService.CurrentStation?.Id
            };
            Model.SettingsJson = WidgetSerializer.Serialize(settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioWidgetViewModel] Failed to save settings: {ex.Message}");
        }
    }

    partial void OnSelectedCategoryIdChanged(string value)
    {
        LoadCategoryStations(value);
        SaveSettings();
    }

    partial void OnVolumeChanged(double value)
    {
        if (_isSyncingVolume) return;
        _audioService.SetVolume(Math.Clamp(value / 100.0, 0.0, 1.0));
    }

    private void LoadCategoryStations(string categoryId)
    {
        VisibleStations.Clear();
        var category = _catalogService.GetCategory(categoryId);
        if (category != null)
        {
            foreach (var station in category.Stations)
            {
                var itemVm = new RadioStationItemViewModel(station)
                {
                    IsActive = CurrentStation?.Id == station.Id,
                    IsPlaying = CurrentStation?.Id == station.Id && IsPlaying,
                    IsBuffering = CurrentStation?.Id == station.Id && IsBuffering
                };
                VisibleStations.Add(itemVm);
            }
        }

        // Append '+' placeholder card at the end of each category
        VisibleStations.Add(RadioStationItemViewModel.CreateAddPlaceholder());
    }

    private void SyncFromAudioService()
    {
        CurrentStation = _audioService.CurrentStation;
        IsPlaying = _audioService.IsPlaying;
        IsBuffering = _audioService.IsBuffering;
        IsMuted = _audioService.IsMuted;

        _isSyncingVolume = true;
        Volume = _audioService.Volume * 100.0;
        _isSyncingVolume = false;

        UpdateDisplayInfo();
        UpdateTileStates();
    }

    private void UpdateDisplayInfo()
    {
        if (CurrentStation != null)
        {
            CurrentStationTitle = CurrentStation.Name;
            CurrentStationBadge = CurrentStation.BitrateKbps > 0 
                ? $"{CurrentStation.BitrateKbps}k" 
                : string.Empty;
        }
        else
        {
            CurrentStationTitle = "Select a station to focus";
            CurrentStationBadge = string.Empty;
        }

        if (IsBuffering)
        {
            PlaybackStatusText = "BUFFERING";
        }
        else if (IsPlaying)
        {
            PlaybackStatusText = "LIVE";
        }
        else if (CurrentStation != null)
        {
            PlaybackStatusText = "PAUSED";
        }
        else
        {
            PlaybackStatusText = "READY";
        }
    }

    private void UpdateTileStates()
    {
        foreach (var item in VisibleStations)
        {
            if (item.IsAddPlaceholder || item.Station == null) continue;
            item.IsActive = CurrentStation?.Id == item.Station.Id;
            item.IsPlaying = item.IsActive && IsPlaying;
            item.IsBuffering = item.IsActive && IsBuffering;
        }
    }

    [RelayCommand]
    private async Task SelectStationAsync(RadioStationItemViewModel? item)
    {
        if (item == null) return;

        if (item.IsAddPlaceholder)
        {
            ErrorMessage = "Custom streams feature coming soon!";
            return;
        }

        if (item.Station == null) return;

        // If clicking the current station, toggle play/pause
        if (_audioService.CurrentStation?.Id == item.Station.Id)
        {
            _audioService.TogglePlayPause();
        }
        else
        {
            await _audioService.PlayStationAsync(item.Station);
            SaveSettings();
        }
    }

    [RelayCommand]
    private async Task TogglePlayPauseAsync()
    {
        if (_audioService.CurrentStation != null)
        {
            _audioService.TogglePlayPause();
            return;
        }

        // If nothing is playing, play the first station of the active category
        var first = VisibleStations.FirstOrDefault(s => !s.IsAddPlaceholder && s.Station != null);
        if (first?.Station != null)
        {
            await _audioService.PlayStationAsync(first.Station);
            SaveSettings();
        }
    }

    [RelayCommand]
    private async Task NextStationAsync()
    {
        var realStations = VisibleStations.Where(s => !s.IsAddPlaceholder && s.Station != null).ToList();
        if (realStations.Count == 0) return;

        int currentIndex = realStations.FindIndex(s => s.Station?.Id == _audioService.CurrentStation?.Id);
        int nextIndex = (currentIndex + 1) % realStations.Count;
        if (nextIndex < 0) nextIndex = 0;

        await _audioService.PlayStationAsync(realStations[nextIndex].Station!);
        SaveSettings();
    }

    [RelayCommand]
    private async Task PreviousStationAsync()
    {
        var realStations = VisibleStations.Where(s => !s.IsAddPlaceholder && s.Station != null).ToList();
        if (realStations.Count == 0) return;

        int currentIndex = realStations.FindIndex(s => s.Station?.Id == _audioService.CurrentStation?.Id);
        int prevIndex = currentIndex <= 0 ? realStations.Count - 1 : currentIndex - 1;

        await _audioService.PlayStationAsync(realStations[prevIndex].Station!);
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleMute()
    {
        _audioService.SetMuted(!_audioService.IsMuted);
    }

    private void OnAudioCurrentStationChanged(object? sender, RadioStation? station)
    {
        DispatchToUi(() =>
        {
            CurrentStation = station;
            UpdateDisplayInfo();
            UpdateTileStates();
        });
    }

    private void OnAudioPlaybackStateChanged(object? sender, bool isPlaying)
    {
        DispatchToUi(() =>
        {
            IsPlaying = isPlaying;
            UpdateDisplayInfo();
            UpdateTileStates();
        });
    }

    private void OnAudioBufferingStateChanged(object? sender, bool isBuffering)
    {
        DispatchToUi(() =>
        {
            IsBuffering = isBuffering;
            UpdateDisplayInfo();
            UpdateTileStates();
        });
    }

    private void OnAudioVolumeChanged(object? sender, double volume)
    {
        DispatchToUi(() =>
        {
            _isSyncingVolume = true;
            Volume = volume * 100.0;
            _isSyncingVolume = false;
        });
    }

    private void OnAudioMuteStateChanged(object? sender, bool isMuted)
    {
        DispatchToUi(() =>
        {
            IsMuted = isMuted;
        });
    }

    private void OnAudioErrorOccurred(object? sender, string error)
    {
        DispatchToUi(() =>
        {
            ErrorMessage = error;
            UpdateDisplayInfo();
        });
    }

    private static void DispatchToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess() && dispatcher.Thread.IsAlive)
        {
            try
            {
                dispatcher.Invoke(action);
                return;
            }
            catch
            {
                // Fall back to direct invocation if dispatcher is inactive or shutting down
            }
        }

        action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _audioService.CurrentStationChanged -= OnAudioCurrentStationChanged;
            _audioService.PlaybackStateChanged -= OnAudioPlaybackStateChanged;
            _audioService.BufferingStateChanged -= OnAudioBufferingStateChanged;
            _audioService.VolumeChanged -= OnAudioVolumeChanged;
            _audioService.MuteStateChanged -= OnAudioMuteStateChanged;
            _audioService.ErrorOccurred -= OnAudioErrorOccurred;
        }
        base.Dispose(disposing);
    }
}
