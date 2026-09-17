using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Core.Services.Catalog.Weather;

namespace MetroHub.Widgets.Catalog.Weather;

public sealed partial class WeatherWidgetViewModel : WidgetViewModelBase
{
    private static readonly TimeSpan BackgroundPollingInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ResumeThreshold = TimeSpan.FromMinutes(15);

    private readonly WeatherService _weatherService = new();
    private System.Threading.Timer? _refreshTimer;
    private CancellationTokenSource? _cts;
    private DateTime _lastFetchTime = DateTime.MinValue;

    private WeatherWidgetSettings _settings = new();
    private WeatherCacheEntry? _latestData;

    // Observable UI properties
    [ObservableProperty]
    private string _cityName = "Locating...";

    [ObservableProperty]
    private string _conditionText = "Updating...";

    [ObservableProperty]
    private string _weatherGlyph = "\uE9C5";

    [ObservableProperty]
    private string _temperatureText = "--°";

    [ObservableProperty]
    private string _feelsLikeText = "--°";

    [ObservableProperty]
    private string _highLowText = "--° / --°";

    [ObservableProperty]
    private string _humidityText = "--%";

    [ObservableProperty]
    private string _windSpeedText = "-- km/h";

    [ObservableProperty]
    private string _precipitationText = "0%";

    [ObservableProperty]
    private string _aqiText = "--";

    [ObservableProperty]
    private string _aqiCategoryText = "AQI --";

    [ObservableProperty]
    private SolidColorBrush _aqiBrush = WeatherPalettes.AqiPalette[AqiCategory.Good].Brush;

    [ObservableProperty]
    private string _pm25Text = "-- µg/m³";

    [ObservableProperty]
    private string _pm10Text = "-- µg/m³";

    [ObservableProperty]
    private string _ozoneText = "-- µg/m³";

    [ObservableProperty]
    private RadialGradientBrush _ambientGlowBrush = WeatherPalettes.CloudyGlow;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool IsFahrenheit => _settings.IsFahrenheit;

    public ObservableCollection<HourlyWeatherItemViewModel> HourlyForecast { get; } = new();
    public ObservableCollection<DailyWeatherItemViewModel> DailyForecast { get; } = new();

    // Responsive sizing flags
    public bool IsCompact => Model.SpanX <= 2 && Model.SpanY <= 2;
    public bool IsWide => Model.SpanX == 4 && Model.SpanY <= 2;
    public bool IsBanner => Model.SpanX >= 6 && Model.SpanY <= 2;
    public bool IsHero => Model.SpanY >= 3;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new List<WidgetSize>
    {
        WidgetSize.Medium,    // 2x2
        WidgetSize.Wide,      // 4x2 (Default)
        WidgetSize.ExtraWide, // 6x2
        WidgetSize.Banner,    // 8x2
        WidgetSize.Banner3,   // 8x3
        WidgetSize.Large,     // 4x4
        WidgetSize.Mega       // 8x4
    };

    public WeatherWidgetViewModel(TileModel model) : base(model)
    {
        Model.PropertyChanged += OnModelPropertyChanged;

        // Load persisted settings
        LoadSettings(model.SettingsJson);

        // Pre-populate with cached disk data instantly
        var cached = _weatherService.GetCachedWeather();
        if (cached != null)
        {
            _latestData = cached;
            _lastFetchTime = cached.TimestampUtc;
            ApplyWeatherToUi(cached);
        }

        // Start 30-minute background timer (paused when hidden)
        Resume();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TileModel.SpanX) or nameof(TileModel.SpanY))
        {
            OnPropertyChanged(nameof(IsCompact));
            OnPropertyChanged(nameof(IsWide));
            OnPropertyChanged(nameof(IsBanner));
            OnPropertyChanged(nameof(IsHero));
        }
    }

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return;
        try
        {
            var parsed = JsonSerializer.Deserialize<WeatherWidgetSettings>(settingsJson);
            if (parsed != null)
            {
                _settings = parsed;
                OnPropertyChanged(nameof(IsFahrenheit));
            }
        }
        catch { }
    }

    public override void SaveSettings()
    {
        try
        {
            Model.SettingsJson = JsonSerializer.Serialize(_settings);
        }
        catch { }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        await FetchWeatherCoreAsync(forceRefresh: true).ConfigureAwait(false);
    }

    [RelayCommand]
    public void ToggleUnits()
    {
        _settings.IsFahrenheit = !_settings.IsFahrenheit;
        SaveSettings();
        OnPropertyChanged(nameof(IsFahrenheit));

        if (_latestData != null)
        {
            ApplyWeatherToUi(_latestData);
        }
    }

    public override void Resume()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        // If resume occurs after threshold (15 minutes), refresh immediately
        if (_latestData == null || (DateTime.UtcNow - _lastFetchTime) >= ResumeThreshold)
        {
            _ = FetchWeatherCoreAsync(forceRefresh: false);
        }

        // Arm 30-minute periodic timer
        _refreshTimer?.Dispose();
        _refreshTimer = new System.Threading.Timer(
            _ => _ = FetchWeatherCoreAsync(forceRefresh: false),
            null,
            BackgroundPollingInterval,
            BackgroundPollingInterval
        );
    }

    public override void Pause()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;

        _cts?.Cancel();
        _cts = null;
    }

    private async Task FetchWeatherCoreAsync(bool forceRefresh)
    {
        var token = _cts?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested) return;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsLoading = true;
            HasError = false;
        });

        try
        {
            // 1. Resolve Location
            double lat;
            double lon;
            string city;

            if (!_settings.IsAutoLocation && _settings.CustomLatitude.HasValue && _settings.CustomLongitude.HasValue)
            {
                lat = _settings.CustomLatitude.Value;
                lon = _settings.CustomLongitude.Value;
                city = _settings.CustomCity ?? "Custom";
            }
            else
            {
                var loc = await _weatherService.Location.ResolveLocationAsync(token).ConfigureAwait(false);
                lat = loc.Latitude;
                lon = loc.Longitude;
                city = string.IsNullOrWhiteSpace(loc.City) ? "Local" : loc.City;
            }

            // 2. Fetch Open-Meteo Data
            var data = await _weatherService.FetchWeatherAsync(lat, lon, city, token).ConfigureAwait(false);
            if (data != null)
            {
                _latestData = data;
                _lastFetchTime = data.TimestampUtc;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ApplyWeatherToUi(data);
                    IsLoading = false;
                });
            }
            else
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsLoading = false;
                    if (_latestData == null)
                    {
                        HasError = true;
                        ErrorMessage = "Offline / No Data";
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Pause
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsLoading = false;
                if (_latestData == null)
                {
                    HasError = true;
                    ErrorMessage = ex.Message;
                }
            });
        }
    }

    private void ApplyWeatherToUi(WeatherCacheEntry entry)
    {
        var current = entry.Forecast?.Current;
        var daily = entry.Forecast?.Daily;
        var hourly = entry.Forecast?.Hourly;
        var aqi = entry.AirQuality?.Current;

        // City
        CityName = entry.City;

        if (current != null)
        {
            // Condition info & Ambient glow
            var (glyph, desc, glow) = WeatherPalettes.GetConditionInfo(current.WeatherCode, current.IsDay == 1);
            WeatherGlyph = glyph;
            ConditionText = desc;
            AmbientGlowBrush = glow;

            // Temperature & Feels-Like
            TemperatureText = FormatTemp(current.Temperature);
            FeelsLikeText = $"Feels like {FormatTemp(current.ApparentTemperature)}";

            // Details
            HumidityText = $"{current.RelativeHumidity}%";
            WindSpeedText = $"{Math.Round(current.WindSpeed)} km/h";
            PrecipitationText = $"{current.Precipitation:0.0} mm";
        }

        // Daily High / Low
        if (daily?.TemperatureMax != null && daily.TemperatureMax.Count > 0 &&
            daily?.TemperatureMin != null && daily.TemperatureMin.Count > 0)
        {
            HighLowText = $"H: {FormatTemp(daily.TemperatureMax[0])}  L: {FormatTemp(daily.TemperatureMin[0])}";
        }

        // AQI
        if (aqi?.UsAqi.HasValue == true)
        {
            int score = aqi.UsAqi.Value;
            var category = WeatherPalettes.GetAqiCategory(score);
            var info = WeatherPalettes.AqiPalette[category];

            AqiText = score.ToString(CultureInfo.InvariantCulture);
            AqiCategoryText = $"AQI {score} · {info.Label}";
            AqiBrush = info.Brush;

            Pm25Text = aqi.Pm25.HasValue ? $"{aqi.Pm25.Value:F1} µg/m³" : "--";
            Pm10Text = aqi.Pm10.HasValue ? $"{aqi.Pm10.Value:F1} µg/m³" : "--";
            OzoneText = aqi.Ozone.HasValue ? $"{aqi.Ozone.Value:F1} µg/m³" : "--";
        }
        else
        {
            AqiCategoryText = "AQI --";
            AqiText = "--";
        }

        // In-place mutation of HourlyForecast (5-12 items)
        if (hourly?.Time != null && hourly.Temperature != null && hourly.WeatherCode != null)
        {
            int count = Math.Min(hourly.Time.Count, 12);
            for (int i = 0; i < count; i++)
            {
                string timeStr = FormatHourlyTime(hourly.Time[i]);
                string tempStr = FormatTemp(hourly.Temperature[i]);
                int wCode = hourly.WeatherCode[i];
                bool isDay = (hourly.IsDay != null && i < hourly.IsDay.Count) ? hourly.IsDay[i] == 1 : true;
                var (hGlyph, _, _) = WeatherPalettes.GetConditionInfo(wCode, isDay);
                int precipProb = (hourly.PrecipitationProbability != null && i < hourly.PrecipitationProbability.Count)
                    ? hourly.PrecipitationProbability[i]
                    : 0;

                if (HourlyForecast.Count > i)
                {
                    HourlyForecast[i].Update(timeStr, tempStr, hGlyph, $"{precipProb}%", precipProb > 20);
                }
                else
                {
                    var item = new HourlyWeatherItemViewModel();
                    item.Update(timeStr, tempStr, hGlyph, $"{precipProb}%", precipProb > 20);
                    HourlyForecast.Add(item);
                }
            }

            while (HourlyForecast.Count > count)
            {
                HourlyForecast.RemoveAt(HourlyForecast.Count - 1);
            }
        }

        // In-place mutation of DailyForecast (5 days)
        if (daily?.Time != null && daily.TemperatureMax != null && daily.TemperatureMin != null && daily.WeatherCode != null)
        {
            int count = Math.Min(daily.Time.Count, 5);
            for (int i = 0; i < count; i++)
            {
                string dayStr = FormatDayOfWeek(daily.Time[i]);
                int wCode = daily.WeatherCode[i];
                var (dGlyph, _, _) = WeatherPalettes.GetConditionInfo(wCode, isDay: true);
                string hlStr = $"{FormatTemp(daily.TemperatureMax[i])} / {FormatTemp(daily.TemperatureMin[i])}";
                double uv = (daily.UvIndexMax != null && i < daily.UvIndexMax.Count) ? daily.UvIndexMax[i] : 0.0;

                if (DailyForecast.Count > i)
                {
                    DailyForecast[i].Update(dayStr, dGlyph, hlStr, uv);
                }
                else
                {
                    var item = new DailyWeatherItemViewModel();
                    item.Update(dayStr, dGlyph, hlStr, uv);
                    DailyForecast.Add(item);
                }
            }

            while (DailyForecast.Count > count)
            {
                DailyForecast.RemoveAt(DailyForecast.Count - 1);
            }
        }
    }

    private string FormatTemp(double tempCelsius)
    {
        if (_settings.IsFahrenheit)
        {
            double fahrenheit = (tempCelsius * 9.0 / 5.0) + 32.0;
            return $"{Math.Round(fahrenheit)}°";
        }
        return $"{Math.Round(tempCelsius)}°";
    }

    private static string FormatHourlyTime(string isoTime)
    {
        if (DateTime.TryParse(isoTime, out var dt))
        {
            return dt.ToString("h tt", CultureInfo.InvariantCulture).ToLowerInvariant();
        }
        return isoTime;
    }

    private static string FormatDayOfWeek(string isoDate)
    {
        if (DateTime.TryParse(isoDate, out var dt))
        {
            return dt.ToString("ddd", CultureInfo.InvariantCulture);
        }
        return isoDate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Model.PropertyChanged -= OnModelPropertyChanged;
            Pause();
        }
        base.Dispose(disposing);
    }
}
