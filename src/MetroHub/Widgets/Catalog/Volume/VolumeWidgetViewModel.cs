using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Audio;
using MetroHub.Core.Models;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Volume;

public partial class AudioDeviceItemViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string IconGlyph { get; init; } = "\uE7F5";

    [ObservableProperty]
    private bool _isDefault;
}

public partial class AppSessionItemViewModel : ObservableObject
{
    private readonly Action<uint, float> _onVolumeChanged;
    private readonly Action<uint, bool> _onMuteChanged;
    private bool _isUpdatingInternally;

    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public ImageSource? IconSource { get; init; }
    public bool IsSystemSounds { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercentText))]
    [NotifyPropertyChangedFor(nameof(VolumeGlyph))]
    private double _volume = 100.0; // 0 - 100

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeGlyph))]
    private bool _isMuted;

    public string VolumePercentText => $"{Math.Round(Volume)}%";

    public string VolumeGlyph
    {
        get
        {
            if (IsMuted) return "\uE74F";
            if (Volume <= 0) return "\uE992";
            if (Volume < 33) return "\uE993";
            if (Volume < 66) return "\uE994";
            return "\uE995";
        }
    }

    public AppSessionItemViewModel(Action<uint, float> onVolumeChanged, Action<uint, bool> onMuteChanged)
    {
        _onVolumeChanged = onVolumeChanged;
        _onMuteChanged = onMuteChanged;
    }

    partial void OnVolumeChanged(double value)
    {
        if (_isUpdatingInternally) return;
        _onVolumeChanged(ProcessId, (float)(Math.Clamp(value, 0.0, 100.0) / 100.0));
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_isUpdatingInternally) return;
        _onMuteChanged(ProcessId, value);
    }

    public void UpdateFromAudioService(float volumeScalar, bool isMuted)
    {
        _isUpdatingInternally = true;
        try
        {
            Volume = Math.Round(volumeScalar * 100.0, 1);
            IsMuted = isMuted;
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }
}

public partial class VolumeWidgetViewModel : WidgetViewModelBase
{
    private readonly AudioService _audioService;
    private bool _isHubVisible = true;
    private bool _isUpdatingMasterInternally;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.SlimWide, // 4x1
        WidgetSize.Mega,     // 8x4
        WidgetSize.Huge      // 8x6
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMixerTab))]
    [NotifyPropertyChangedFor(nameof(IsDevicesTab))]
    private string _currentTab = "Mixer";

    public bool IsMixerTab => string.Equals(CurrentTab, "Mixer", StringComparison.OrdinalIgnoreCase);
    public bool IsDevicesTab => string.Equals(CurrentTab, "Devices", StringComparison.OrdinalIgnoreCase);

    public bool IsCompactMode => Model.SpanY <= 1;

    public ObservableCollection<AudioDeviceItemViewModel> Devices { get; } = new();
    public ObservableCollection<AppSessionItemViewModel> AppSessions { get; } = new();

    public VolumeWidgetViewModel(TileModel model) : base(model)
    {
        _audioService = AudioService.Instance;

        _audioService.MasterVolumeChanged += OnAudioServiceMasterVolumeChanged;
        _audioService.DefaultDeviceChanged += OnAudioServiceDefaultDeviceChanged;
        _audioService.DeviceListChanged += OnAudioServiceDeviceListChanged;
        _audioService.SessionsChanged += OnAudioServiceSessionsChanged;

        RefreshAll();
    }

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return;
        try
        {
            var settings = WidgetSerializer.Deserialize<VolumeWidgetSettings>(settingsJson);
            if (settings != null && !string.IsNullOrWhiteSpace(settings.DefaultTab))
            {
                CurrentTab = settings.DefaultTab;
            }
        }
        catch { }
    }

    public override void SaveSettings()
    {
        var settings = new VolumeWidgetSettings
        {
            DefaultTab = CurrentTab
        };
        Model.TargetPath = "volume";
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
    public void SwitchTab(string tab)
    {
        CurrentTab = tab;
        SaveSettings();
    }

    [RelayCommand]
    public void SelectDevice(AudioDeviceItemViewModel? device)
    {
        if (device == null || device.IsDefault) return;

        _audioService.SetDefaultPlaybackDevice(device.Id);
    }

    [RelayCommand]
    public void ToggleAppMute(AppSessionItemViewModel? session)
    {
        if (session == null) return;
        session.IsMuted = !session.IsMuted;
    }

    public void RefreshAll()
    {
        if (!_isHubVisible) return;

        RefreshMasterVolume();
        RefreshDevices();
        RefreshAppSessions();
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

    private void RefreshAppSessions()
    {
        var rawSessions = _audioService.GetAppSessions();

        AppSessions.Clear();
        foreach (var s in rawSessions)
        {
            var item = new AppSessionItemViewModel(
                onVolumeChanged: (pid, vol) => _audioService.SetAppVolume(pid, vol),
                onMuteChanged: (pid, mute) => _audioService.SetAppMute(pid, mute))
            {
                ProcessId = s.ProcessId,
                ProcessName = s.ProcessName,
                DisplayName = s.DisplayName,
                IconSource = s.IconSource,
                IsSystemSounds = s.IsSystemSounds
            };
            item.UpdateFromAudioService(s.Volume, s.IsMuted);
            AppSessions.Add(item);
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

    private void OnAudioServiceSessionsChanged()
    {
        if (!_isHubVisible) return;
        Application.Current?.Dispatcher.InvokeAsync(RefreshAppSessions);
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
            _audioService.MasterVolumeChanged -= OnAudioServiceMasterVolumeChanged;
            _audioService.DefaultDeviceChanged -= OnAudioServiceDefaultDeviceChanged;
            _audioService.DeviceListChanged -= OnAudioServiceDeviceListChanged;
            _audioService.SessionsChanged -= OnAudioServiceSessionsChanged;

            Devices.Clear();
            AppSessions.Clear();
        }

        base.Dispose(disposing);
    }
}
