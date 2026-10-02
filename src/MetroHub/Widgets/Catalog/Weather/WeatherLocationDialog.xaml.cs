using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MetroHub.Core.Models.Geocoding;
using MetroHub.Core.Services;
using MetroHub.Presentation.Dialogs;
using MetroHub.Widgets.Catalog.Weather;
using Wpf.Ui.Controls;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Dedicated dialog for searching, previewing, and setting weather location coordinates.
/// Inherits from MetroDialog for automatic acrylic backdrop, window dragging, and styling.
/// </summary>
public partial class WeatherLocationDialog : MetroDialog
{
    private WeatherWidgetViewModel? _weatherVm;
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private bool _suppressSearch;
    private string? _activeKey;
    private GeoResult? _selectedLocation;

    internal ObservableCollection<GeoResult> Suggestions { get; } = new();

    public WeatherLocationDialog()
    {
        InitializeComponent();
        WeatherSuggestionsList.ItemsSource = Suggestions;
        IsVisibleChanged += (s, e) =>
        {
            if (!IsVisible)
            {
                CancelPendingSearch();
                HideSuggestions();
                WeatherLocationService.Instance.ClearCache();
            }
        };
    }

    public void Setup(WeatherWidgetViewModel weatherVm)
    {
        _weatherVm = weatherVm;
        _suppressSearch = true;
        _selectedLocation = null;

        string currentCity = weatherVm?.CityName ?? string.Empty;
        WeatherCityInput.Text = currentCity;
        _suppressSearch = false;

        WeatherStatusMessage.Visibility = Visibility.Collapsed;
        WeatherActionSpinner.Visibility = Visibility.Collapsed;
    }

    private async void OnWeatherInputTextChanged(object sender, TextChangedEventArgs e)
    {
        WeatherStatusMessage.Visibility = Visibility.Collapsed;
        if (_suppressSearch) return;

        var raw = WeatherCityInput.Text;
        string? normalized = WeatherLocationService.NormalizeQuery(raw);

        if (string.IsNullOrEmpty(normalized))
        {
            CancelPendingSearch();
            HideSuggestions();
            _selectedLocation = null;
            return;
        }

        var (location, _) = WeatherLocationService.ParseQuery(normalized);
        if (!WeatherLocationService.LongEnough(location))
        {
            CancelPendingSearch();
            HideSuggestions();
            _selectedLocation = null;
            return;
        }

        if (string.Equals(normalized, _activeKey, StringComparison.Ordinal))
        {
            return;
        }

        CancelPendingSearch();
        _selectedLocation = null;

        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        int generation = ++_searchGeneration;
        _activeKey = normalized;

        try
        {
            await Task.Delay(300, token);
            if (generation != _searchGeneration || token.IsCancellationRequested) return;

            var results = await WeatherLocationService.Instance.SearchLocationsAsync(normalized, token);
            if (generation != _searchGeneration || token.IsCancellationRequested) return;

            ShowSuggestions(results);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            WeatherStatusMessage.Text = $"Location search failed: {ex.Message}";
            WeatherStatusMessage.Visibility = Visibility.Visible;
        }
    }

    private void ShowSuggestions(IReadOnlyList<GeoResult> results)
    {
        Suggestions.Clear();
        foreach (var r in results)
        {
            Suggestions.Add(r);
        }

        if (Suggestions.Count > 0)
        {
            WeatherSuggestionsPopup.IsOpen = true;
            WeatherSuggestionsList.SelectedIndex = 0;
        }
        else
        {
            HideSuggestions();
        }
    }

    private void HideSuggestions()
    {
        WeatherSuggestionsPopup.IsOpen = false;
        Suggestions.Clear();
    }

    private void CancelPendingSearch()
    {
        _activeKey = null;
        _searchGeneration++;
        var cts = _searchCts;
        _searchCts = null;
        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch { }
    }

    private void OnWeatherInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && WeatherSuggestionsPopup.IsOpen)
        {
            WeatherSuggestionsList.Focus();
            if (WeatherSuggestionsList.SelectedIndex < 0)
            {
                WeatherSuggestionsList.SelectedIndex = 0;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (WeatherSuggestionsPopup.IsOpen && WeatherSuggestionsList.SelectedItem is GeoResult selected)
            {
                SelectSuggestion(selected);
                e.Handled = true;
            }
            else
            {
                OnSaveLocationClick(sender, e);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (WeatherSuggestionsPopup.IsOpen)
            {
                HideSuggestions();
                e.Handled = true;
            }
            else
            {
                DialogResult = false;
                Close();
            }
        }
    }

    private void OnSuggestionsListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && WeatherSuggestionsList.SelectedItem is GeoResult selected)
        {
            SelectSuggestion(selected);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideSuggestions();
            WeatherCityInput.Focus();
            e.Handled = true;
        }
    }

    private void OnSuggestionListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (WeatherSuggestionsList.SelectedItem is GeoResult selected)
        {
            SelectSuggestion(selected);
        }
    }

    private void SelectSuggestion(GeoResult selected)
    {
        _suppressSearch = true;
        _selectedLocation = selected;

        string display = !string.IsNullOrWhiteSpace(selected.DisplaySubtitle)
            ? $"{selected.Name}, {selected.DisplaySubtitle}"
            : selected.Name;

        WeatherCityInput.Text = display;
        WeatherCityInput.CaretIndex = display.Length;
        HideSuggestions();
        _suppressSearch = false;
    }

    private async void OnWeatherAutoLocationClick(object sender, RoutedEventArgs e)
    {
        if (_weatherVm == null) return;

        HideSuggestions();
        CancelPendingSearch();

        WeatherActionSpinner.Visibility = Visibility.Visible;
        WeatherStatusMessage.Visibility = Visibility.Collapsed;

        try
        {
            await _weatherVm.UseAutoLocationAsync();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            WeatherActionSpinner.Visibility = Visibility.Collapsed;
            WeatherStatusMessage.Text = $"Error: {ex.Message}";
            WeatherStatusMessage.Visibility = Visibility.Visible;
        }
    }

    private async void OnSaveLocationClick(object sender, RoutedEventArgs e)
    {
        if (_weatherVm == null) return;
        string query = WeatherCityInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            WeatherStatusMessage.Text = "Please enter a city or location name.";
            WeatherStatusMessage.Visibility = Visibility.Visible;
            WeatherCityInput.Focus();
            return;
        }

        HideSuggestions();
        CancelPendingSearch();

        WeatherActionSpinner.Visibility = Visibility.Visible;
        WeatherStatusMessage.Visibility = Visibility.Collapsed;

        try
        {
            bool success;
            if (_selectedLocation != null &&
                _selectedLocation.Latitude.HasValue &&
                _selectedLocation.Longitude.HasValue &&
                string.Equals(_selectedLocation.Name.Trim(), query, StringComparison.OrdinalIgnoreCase))
            {
                success = await _weatherVm.SetCustomLocationAsync(_selectedLocation.Name, _selectedLocation.Latitude.Value, _selectedLocation.Longitude.Value);
            }
            else
            {
                success = await _weatherVm.SetCustomCityAsync(query);
            }

            WeatherActionSpinner.Visibility = Visibility.Collapsed;

            if (success)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                WeatherStatusMessage.Text = $"City \"{query}\" not found. Try including region/country.";
                WeatherStatusMessage.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            WeatherActionSpinner.Visibility = Visibility.Collapsed;
            WeatherStatusMessage.Text = $"Search failed: {ex.Message}";
            WeatherStatusMessage.Visibility = Visibility.Visible;
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static bool Show(Window? owner, WeatherWidgetViewModel weatherVm)
    {
        var dlg = new WeatherLocationDialog
        {
            Owner = owner
        };
        dlg.Setup(weatherVm);
        return dlg.ShowDialog() == true;
    }
}
