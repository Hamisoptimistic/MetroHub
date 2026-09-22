using System;
using System.Collections.Generic;
using System.ComponentModel;
using MetroHub.Core.Models;
using MetroHub.Widgets.Catalog.BrightnessControls;
using MetroHub.Widgets.Catalog.Media;
using MetroHub.Widgets.Catalog.AudioControls;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.QuickControls;

public sealed partial class QuickControlsWidgetViewModel : WidgetViewModelBase
{
    private readonly TileModel _mediaSubModel;
    private readonly TileModel _volumeSubModel;
    private readonly TileModel _brightnessSubModel;

    public MediaWidgetViewModel Media { get; }
    public AudioControlsWidgetViewModel Volume { get; }
    public BrightnessControlsWidgetViewModel Brightness { get; }

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Wide3,      // 4x3
        WidgetSize.ExtraWide3, // 6x3
        WidgetSize.Banner3     // 8x3
    };

    public QuickControlsWidgetViewModel(TileModel model) : base(model)
    {
        _mediaSubModel = new TileModel
        {
            Id = model.Id + "_media",
            SpanX = model.SpanX,
            SpanY = 1,
            TargetPath = "media"
        };
        _volumeSubModel = new TileModel
        {
            Id = model.Id + "_volume",
            SpanX = model.SpanX,
            SpanY = 1,
            TargetPath = "audio_controls"
        };
        _brightnessSubModel = new TileModel
        {
            Id = model.Id + "_brightness",
            SpanX = model.SpanX,
            SpanY = 1,
            TargetPath = "brightness_controls"
        };

        Media = new MediaWidgetViewModel(_mediaSubModel);
        Volume = new AudioControlsWidgetViewModel(_volumeSubModel);
        Brightness = new BrightnessControlsWidgetViewModel(_brightnessSubModel);

        Model.PropertyChanged += OnModelPropertyChanged;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TileModel.SpanX))
        {
            _mediaSubModel.SpanX = Model.SpanX;
            _volumeSubModel.SpanX = Model.SpanX;
            _brightnessSubModel.SpanX = Model.SpanX;
        }
    }

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return;
        try
        {
            var settings = WidgetSerializer.Deserialize<QuickControlsWidgetSettings>(settingsJson);
        }
        catch { }
    }

    public override void SaveSettings()
    {
        var settings = new QuickControlsWidgetSettings();
        Model.TargetPath = "quick_controls";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
    }

    public override void Pause()
    {
        Media.Pause();
        Volume.Pause();
        Brightness.Pause();
    }

    public override void Resume()
    {
        Media.Resume();
        Volume.Resume();
        Brightness.Resume();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Model.PropertyChanged -= OnModelPropertyChanged;
            Media.Dispose();
            Volume.Dispose();
            Brightness.Dispose();
        }
        base.Dispose(disposing);
    }
}
