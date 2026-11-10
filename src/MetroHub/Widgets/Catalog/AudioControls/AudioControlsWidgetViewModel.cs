using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Audio;
using MetroHub.Core.Models;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.AudioControls;

public sealed partial class AudioDeviceItemViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string IconGlyph { get; init; } = "\uE7F5";

    [ObservableProperty]
    private bool _isDefault;
}

public sealed partial class AudioControlsWidgetViewModel : WidgetViewModelBase
{
    private readonly AudioService _audioService;
    private bool _isHubVisible = true;
    private bool _isUpdatingMasterInternally;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.SlimWide,    // 4x1
        WidgetSize.SlimBanner,  // 8x1
        WidgetSize.LargeWide,   // 6x4
        WidgetSize.SquareLarge, // 6x6
        WidgetSize.Mega,        // 8x4
        WidgetSize.Huge         // 8x6
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterVolumePercentText))]
    [NotifyPropertyChangedFor(nameof(MasterGlyph))]
    private double _masterVolume = 50.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterGlyph))]
    private bool _isMasterMuted;

    public string MasterVolumePercentText => $"{Math.Round(MasterVolume)}%";

    public string MasterGlyph
    {
        get
        {
            if (IsMasterMuted) return "\uE74F"; // Mute
            if (MasterVolume <= 0) return "\uE992"; // Speaker 0
            if (MasterVolume < 33) return "\uE993"; // Speaker 1
            if (MasterVolume < 66) return "\uE994"; // Speaker 2
            return "\uE995"; // Speaker 3
        }
    }

    [ObservableProperty]
    private string _activeDeviceName = "Audio Endpoint";

    [ObservableProperty]
    private string _activeDeviceIcon = "\uE7F5";

    public bool IsCompactMode => Model.SpanY <= 1;

    public ObservableCollection<AudioDeviceItemViewModel> Devices { get; } = new();

    public AudioControlsWidgetViewModel(TileModel model) : base(model)
    {
        _audioService = AudioService.Instance;

        _audioService.MasterVolumeChanged += OnAudioServiceMasterVolumeChanged;
        _audioService.DefaultDeviceChanged += OnAudioServiceDefaultDeviceChanged;
        _audioService.DeviceListChanged += OnAudioServiceDeviceListChanged;

        Model.PropertyChanged += OnModelPropertyChanged;

        RefreshAll();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TileModel.SpanX) or nameof(TileModel.SpanY))
        {
            OnPropertyChanged(nameof(IsCompactMode));
        }
    }

    protected override void LoadSettings(string? settingsJson) { }

    public override void SaveSettings()
    {
        var settings = new AudioControlsWidgetSettings();
        Model.TargetPath = "audio_controls";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
    }

    partial void OnMasterVolumeChanged(double value)
    {
        if (_isUpdatingMasterInternally) return;
        _audioService.SetMasterVolume((float)(Math.Clamp(value, 0.0, 100.0) / 100.0));
    }

    partial void OnIsMasterMutedChanged(bool value)
    {
        if (_isUpdatingMasterInternally) return;
        _audioService.SetMasterMute(value);
    }

    [RelayCommand]
    public void ToggleMasterMute()
    {
        IsMasterMuted = !IsMasterMuted;
    }

    [RelayCommand]
    public void ToggleNextDevice()
    {
        if (Devices.Count <= 1)
        {
            RefreshDevices();
            if (Devices.Count <= 1) return;
        }

        int currentIndex = -1;
        for (int i = 0; i < Devices.Count; i++)
        {
            if (Devices[i].IsDefault)
            {
                currentIndex = i;
                break;
            }
        }

        int nextIndex = currentIndex >= 0 ? (currentIndex + 1) % Devices.Count : 0;
        SelectDevice(Devices[nextIndex]);
    }

    [RelayCommand]
    public void SelectDevice(AudioDeviceItemViewModel? device)
    {
        if (device == null || device.IsDefault) return;

        // Optimistic UI update for instant feedback
        foreach (var d in Devices)
        {
            d.IsDefault = string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase);
        }
        ActiveDeviceName = device.Name;
        ActiveDeviceIcon = device.IconGlyph;

        Task.Run(() =>
        {
            try
            {
                _audioService.SetDefaultPlaybackDevice(device.Id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioControlsWidget] SetDefaultPlaybackDevice failed: {ex.Message}");
            }
        });
    }

    public void RefreshAll()
    {
        if (!_isHubVisible) return;

        RefreshMasterVolume();
        RefreshDevices();
    }

    private void RefreshMasterVolume()
    {
        float vol = _audioService.GetMasterVolume();
        bool muted = _audioService.GetMasterMute();

        _isUpdatingMasterInternally = true;
        try
        {
            MasterVolume = Math.Round(vol * 100.0, 1);
            IsMasterMuted = muted;
        }
        finally
        {
            _isUpdatingMasterInternally = false;
        }
    }

    private void RefreshDevices()
    {
        var rawDevices = _audioService.GetPlaybackDevices();
        var defaultDev = rawDevices.FirstOrDefault(d => d.IsDefault);

        if (defaultDev != null)
        {
            ActiveDeviceName = defaultDev.Name;
            ActiveDeviceIcon = defaultDev.IconGlyph;
        }
        else
        {
            ActiveDeviceName = "Audio Endpoint";
            ActiveDeviceIcon = "\uE7F5";
        }

        // In-place update if device list matches to prevent list flicker
        if (Devices.Count == rawDevices.Count && Devices.Select(d => d.Id).SequenceEqual(rawDevices.Select(d => d.Id), StringComparer.OrdinalIgnoreCase))
        {
            for (int i = 0; i < Devices.Count; i++)
            {
                Devices[i].IsDefault = rawDevices[i].IsDefault;
            }
        }
        else
        {
            Devices.Clear();
            foreach (var dev in rawDevices)
            {
                Devices.Add(new AudioDeviceItemViewModel
                {
                    Id = dev.Id,
                    Name = dev.Name,
                    IconGlyph = dev.IconGlyph,
                    IsDefault = dev.IsDefault
                });
            }
        }
    }

    private void OnAudioServiceMasterVolumeChanged(float volume, bool isMuted)
    {
        if (!_isHubVisible) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _isUpdatingMasterInternally = true;
            try
            {
                MasterVolume = Math.Round(volume * 100.0, 1);
                IsMasterMuted = isMuted;
            }
            finally
            {
                _isUpdatingMasterInternally = false;
            }
        });
    }

    private void OnAudioServiceDefaultDeviceChanged()
    {
        if (!_isHubVisible) return;
        Application.Current?.Dispatcher.InvokeAsync(RefreshAll);
    }

    private void OnAudioServiceDeviceListChanged()
    {
        if (!_isHubVisible) return;
        Application.Current?.Dispatcher.InvokeAsync(RefreshDevices);
    }

    public override void Pause()
    {
        _isHubVisible = false;
    }

    public override void Resume()
    {
        _isHubVisible = true;
        RefreshAll();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Model.PropertyChanged -= OnModelPropertyChanged;
            _audioService.MasterVolumeChanged -= OnAudioServiceMasterVolumeChanged;
            _audioService.DefaultDeviceChanged -= OnAudioServiceDefaultDeviceChanged;
            _audioService.DeviceListChanged -= OnAudioServiceDeviceListChanged;

            Devices.Clear();
        }

        base.Dispose(disposing);
    }
}
