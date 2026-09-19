using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MetroHub.Widgets.Catalog.Weather;

namespace MetroHub.Presentation.Controls;

public sealed partial class SetWeatherCityDialogControl : UserControl
{
    private WeatherWidgetViewModel? _targetVm;

    public event EventHandler? CityTextChanged;
    public event EventHandler? EnterPressed;
    public event EventHandler? AutoLocationRequested;

    public string CityName => CityTextBox.Text.Trim();

    public SetWeatherCityDialogControl()
    {
        InitializeComponent();
    }

    public void Initialize(WeatherWidgetViewModel targetVm)
    {
        _targetVm = targetVm;

        // Update current location indicator
        string currentCity = !string.IsNullOrWhiteSpace(targetVm.CityName) ? targetVm.CityName : "Local";
        CurrentLocationText.Text = currentCity;

        if (targetVm.IsAutoLocation)
        {
            CurrentLocationBadgeText.Text = "AUTO";
            CurrentLocationBadge.Background = new SolidColorBrush(Color.FromArgb(0x25, 0x60, 0xCD, 0xFF));
            CurrentLocationBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
        }
        else
        {
            CurrentLocationBadgeText.Text = "CUSTOM";
            CurrentLocationBadge.Background = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xA0, 0x00));
            CurrentLocationBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00));
        }

        CityTextBox.Text = targetVm.CustomCity ?? string.Empty;
        StatusMessageText.Text = string.Empty;
        StatusMessageText.Visibility = Visibility.Collapsed;
        SearchProgressBar.Visibility = Visibility.Collapsed;
    }

    public void FocusInput()
    {
        CityTextBox.Focus();
        CityTextBox.SelectAll();
    }

    public async Task<bool> ApplyAsync()
    {
        if (_targetVm == null) return false;
        string query = CityName;
        if (string.IsNullOrWhiteSpace(query)) return false;

        SearchProgressBar.Visibility = Visibility.Visible;
        StatusMessageText.Visibility = Visibility.Collapsed;

        try
        {
            bool success = await _targetVm.SetCustomCityAsync(query);
            SearchProgressBar.Visibility = Visibility.Collapsed;

            if (success)
            {
                return true;
            }
            else
            {
                StatusMessageText.Text = $"City \"{query}\" could not be found. Try adding a country or region (e.g. \"Paris, France\").";
                StatusMessageText.Visibility = Visibility.Visible;
                return false;
            }
        }
        catch (Exception ex)
        {
            SearchProgressBar.Visibility = Visibility.Collapsed;
            StatusMessageText.Text = $"Search failed: {ex.Message}";
            StatusMessageText.Visibility = Visibility.Visible;
            return false;
        }
    }

    public async Task ApplyAutoLocationAsync()
    {
        if (_targetVm == null) return;
        SearchProgressBar.Visibility = Visibility.Visible;
        try
        {
            await _targetVm.UseAutoLocationAsync();
        }
        catch { }
        finally
        {
            SearchProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCityTextChanged(object sender, TextChangedEventArgs e)
    {
        StatusMessageText.Visibility = Visibility.Collapsed;
        CityTextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCityKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EnterPressed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnAutoLocationClick(object sender, RoutedEventArgs e)
    {
        AutoLocationRequested?.Invoke(this, EventArgs.Empty);
    }
}
