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
    private CancellationTokenSource? _cts;
    private Task? _refreshLoopTask;
    private int _isFetching;
    private DateTime _lastFetchTime = DateTime.MinValue;

    private WeatherWidgetSettings _settings = new();
    private WeatherCacheEntry? _latestData;

    // Observable UI properties
    [ObservableProperty]
    private string _cityName = "Locating...";

    [ObservableProperty]
    private string _conditionText = "Updating...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WeatherIconSource))]
    private string _weatherIconName = "partly-cloudy-day";

    public ImageSource WeatherIconSource => WeatherIcons.Get(WeatherIconName);

    public ImageSource UvIndexIconSource => WeatherIcons.Get("uv-index");

    [ObservableProperty]
    private string _weatherGlyph = "\uE9C5";

    [ObservableProperty]
    private string _temperatureText = "--°";

    [ObservableProperty]
    private string _feelsLikeText = "--°";

    [ObservableProperty]
    private string _feelsLikeShortText = "--°";

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
    private string _highTempText = "--°";

    [ObservableProperty]
    private string _lowTempText = "--°";

    [ObservableProperty]
    private string _uvIndexText = "--";

    [ObservableProperty]
    private double _uvIndexValue;

    [ObservableProperty]
    private string _uvCategoryText = "UV --";

    [ObservableProperty]
    private SolidColorBrush _uvBrush = WeatherPalettes.UvPalette[UvCategory.Low].Brush;

    [ObservableProperty]
    private int _precipitationProbability;

    [ObservableProperty]
    private string _precipitationProbabilityText = "0%";

    [ObservableProperty]
    private Brush _ambientGlowBrush = WeatherPalettes.CloudyGlow;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool IsFahrenheit => _settings.IsFahrenheit;
    public bool IsAutoLocation => _settings.IsAutoLocation;
    public string? CustomCity => _settings.CustomCity;
    public bool IsAmbientGlowEnabled => _settings.IsAmbientGlowEnabled;

    // Responsive sizing flags
    public bool IsCompact => false;
    public bool IsWide => Model.SpanX <= 4 && Model.SpanY <= 2;
    public bool IsSquareLarge => Model.SpanX == 4 && Model.SpanY >= 3;
    public bool IsBanner => Model.SpanX == 8 && Model.SpanY == 2;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new List<WidgetSize>
    {
        WidgetSize.Wide,      // 4x2 (Default)
        WidgetSize.Large,     // 4x4
        WidgetSize.Banner     // 8x2
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
            OnPropertyChanged(nameof(IsSquareLarge));
            OnPropertyChanged(nameof(IsBanner));
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
                OnPropertyChanged(nameof(IsAmbientGlowEnabled));
                if (!_settings.IsAmbientGlowEnabled)
                {
                    AmbientGlowBrush = System.Windows.Media.Brushes.Transparent;
                }
            }
        }
        catch { }
    }

    public void SetAmbientGlow(bool enabled)
    {
        if (_settings.IsAmbientGlowEnabled == enabled) return;
        _settings.IsAmbientGlowEnabled = enabled;
        SaveSettings();
        OnPropertyChanged(nameof(IsAmbientGlowEnabled));

        if (_latestData != null)
        {
            ApplyWeatherToUi(_latestData);
        }
        else
        {
            AmbientGlowBrush = enabled ? WeatherPalettes.CloudyGlow : System.Windows.Media.Brushes.Transparent;
        }
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
        var token = _cts?.Token ?? CancellationToken.None;
        await FetchWeatherCoreAsync(forceRefresh: true, token).ConfigureAwait(false);
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

    public async Task<bool> SetCustomCityAsync(string cityName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cityName)) return false;

        var token = _cts?.Token ?? cancellationToken;
        var location = await _weatherService.Location.SearchCityAsync(cityName.Trim(), token).ConfigureAwait(false);
        if (location == null) return false;

        _settings.IsAutoLocation = false;
        _settings.CustomCity = location.City;
        _settings.CustomLatitude = location.Latitude;
        _settings.CustomLongitude = location.Longitude;
        SaveSettings();

        OnPropertyChanged(nameof(IsAutoLocation));
        OnPropertyChanged(nameof(CustomCity));

        await FetchWeatherCoreAsync(forceRefresh: true, token).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SetCustomLocationAsync(string cityName, double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cityName)) return false;

        _settings.IsAutoLocation = false;
        _settings.CustomCity = cityName.Trim();
        _settings.CustomLatitude = Math.Round(latitude, 4);
        _settings.CustomLongitude = Math.Round(longitude, 4);
        SaveSettings();

        OnPropertyChanged(nameof(IsAutoLocation));
        OnPropertyChanged(nameof(CustomCity));

        var token = _cts?.Token ?? cancellationToken;
        await FetchWeatherCoreAsync(forceRefresh: true, token).ConfigureAwait(false);
        return true;
    }

    public async Task UseAutoLocationAsync(CancellationToken cancellationToken = default)
    {
        _settings.IsAutoLocation = true;
        _settings.CustomCity = null;
        _settings.CustomLatitude = null;
        _settings.CustomLongitude = null;
        SaveSettings();

        OnPropertyChanged(nameof(IsAutoLocation));
        OnPropertyChanged(nameof(CustomCity));

        var token = _cts?.Token ?? cancellationToken;
        await FetchWeatherCoreAsync(forceRefresh: true, token).ConfigureAwait(false);
    }

    public override void Resume()
    {
        Pause(); // Clean up existing loop/tokens if resuming

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // If resume occurs after threshold (15 minutes), refresh immediately
        if (_latestData == null || (DateTime.UtcNow - _lastFetchTime) >= ResumeThreshold)
        {
            _ = FetchWeatherCoreAsync(forceRefresh: false, token);
        }

        // Arm 30-minute async periodic loop (zero ThreadPool thread hopping, 0 CPU idle)
        _refreshLoopTask = RunPeriodicRefreshLoopAsync(token);
    }

    public override void Pause()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _refreshLoopTask = null;
    }

    private async Task RunPeriodicRefreshLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(BackgroundPollingInterval);
            while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await FetchWeatherCoreAsync(forceRefresh: false, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Pause
        }
    }

    private async Task FetchWeatherCoreAsync(bool forceRefresh, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return;

        // Concurrency guard: prevent simultaneous fetches from manual refresh vs timer tick
        if (Interlocked.CompareExchange(ref _isFetching, 1, 0) != 0)
        {
            return;
        }

        // Silent background refresh: only toggle loading indicator on manual user requests or initial load
        bool shouldShowLoading = forceRefresh || _latestData == null;
        if (shouldShowLoading)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsLoading = true;
                HasError = false;
            });
        }

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
            if (token.IsCancellationRequested) return;

            if (data != null)
            {
                _latestData = data;
                _lastFetchTime = data.TimestampUtc;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ApplyWeatherToUi(data);
                    if (shouldShowLoading) IsLoading = false;
                });
            }
            else
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (shouldShowLoading) IsLoading = false;
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
                if (shouldShowLoading) IsLoading = false;
                if (_latestData == null)
                {
                    HasError = true;
                    ErrorMessage = ex.Message;
                }
            });
        }
        finally
        {
            Interlocked.Exchange(ref _isFetching, 0);
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
            var info = WeatherPalettes.GetConditionInfo(current.WeatherCode, current.IsDay == 1);
            WeatherIconName = info.IconName;
            WeatherGlyph = info.FallbackGlyph;
            ConditionText = info.Description;
            AmbientGlowBrush = _settings.IsAmbientGlowEnabled ? info.Glow : System.Windows.Media.Brushes.Transparent;

            // Temperature & Feels-Like
            TemperatureText = FormatTemp(current.Temperature);
            FeelsLikeText = $"Feels like {FormatTemp(current.ApparentTemperature)}";
            FeelsLikeShortText = FormatTemp(current.ApparentTemperature);

            // Details
            HumidityText = $"{current.RelativeHumidity}%";
            WindSpeedText = $"{Math.Round(current.WindSpeed)} km/h";
            PrecipitationText = $"{current.Precipitation:0.0} mm";

            // UV Index
            double uv = current.UvIndex;
            UvIndexValue = uv;
            var uvCat = WeatherPalettes.GetUvCategory(uv);
            var uvInfo = WeatherPalettes.UvPalette[uvCat];
            UvIndexText = $"{uv:F1}";
            UvCategoryText = $"{uvInfo.Label} ({uv:F1})";
            UvBrush = uvInfo.Brush;
        }

        // Daily High / Low
        if (daily?.TemperatureMax != null && daily.TemperatureMax.Count > 0 &&
            daily?.TemperatureMin != null && daily.TemperatureMin.Count > 0)
        {
            HighTempText = FormatTemp(daily.TemperatureMax[0]);
            LowTempText = FormatTemp(daily.TemperatureMin[0]);
            HighLowText = $"H: {HighTempText}  L: {LowTempText}";
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

        // Precipitation Probability for compact & 4x4 cards
        if (hourly?.PrecipitationProbability != null && hourly.PrecipitationProbability.Count > 0)
        {
            int precipProb = hourly.PrecipitationProbability[0];
            PrecipitationProbability = precipProb;
            PrecipitationProbabilityText = $"{precipProb}%";
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
