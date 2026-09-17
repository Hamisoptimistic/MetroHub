using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Catalog.Weather;

public sealed partial class HourlyWeatherItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _timeString = string.Empty;

    [ObservableProperty]
    private string _temperatureText = string.Empty;

    [ObservableProperty]
    private string _glyph = "\uE9C5";

    [ObservableProperty]
    private string _precipitationText = string.Empty;

    [ObservableProperty]
    private bool _hasPrecipitation;

    public void Update(string timeString, string temperatureText, string glyph, string precipitationText, bool hasPrecipitation)
    {
        if (TimeString != timeString) TimeString = timeString;
        if (TemperatureText != temperatureText) TemperatureText = temperatureText;
        if (Glyph != glyph) Glyph = glyph;
        if (PrecipitationText != precipitationText) PrecipitationText = precipitationText;
        if (HasPrecipitation != hasPrecipitation) HasPrecipitation = hasPrecipitation;
    }
}

public sealed partial class DailyWeatherItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _dayString = string.Empty;

    [ObservableProperty]
    private string _glyph = "\uE9C5";

    [ObservableProperty]
    private string _highLowText = string.Empty;

    [ObservableProperty]
    private double _uvIndex;

    public void Update(string dayString, string glyph, string highLowText, double uvIndex)
    {
        if (DayString != dayString) DayString = dayString;
        if (Glyph != glyph) Glyph = glyph;
        if (HighLowText != highLowText) HighLowText = highLowText;
        if (UvIndex != uvIndex) UvIndex = uvIndex;
    }
}
