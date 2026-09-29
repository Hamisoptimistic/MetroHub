using System;
using System.Windows;
using MetroHub.Core.Radio;
using MetroHub.Widgets.Catalog.Weather;
using Wpf.Ui.Controls;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Modal dialog modes supported by MetroHub.
/// </summary>
public enum AcrylicModalMode
{
    WeatherLocation,
    AddWebLink,
    AddRadioStation
}

/// <summary>
/// Backward-compatible facade for modal dialogs.
/// Delegates directly to specialized, decoupled dialog implementations:
/// <see cref="WeatherLocationDialog"/>, <see cref="WebLinkDialog"/>, and <see cref="RadioStationDialog"/>.
/// </summary>
public partial class AcrylicModalWindow : FluentWindow
{
    public AcrylicModalWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the weather location modal dialog.
    /// </summary>
    public static bool ShowWeatherLocation(Window owner, WeatherWidgetViewModel weatherVm)
    {
        return WeatherLocationDialog.Show(owner, weatherVm);
    }

    /// <summary>
    /// Shows the web link shortcut creation modal dialog.
    /// </summary>
    public static WebLinkCreatedEventArgs? ShowAddWebLink(Window owner, string? initialUrl = null)
    {
        return WebLinkDialog.Show(owner, initialUrl);
    }

    /// <summary>
    /// Shows the add radio station modal dialog.
    /// </summary>
    public static RadioStation? ShowAddRadioStation(Window? owner, string activeCategoryId = "ambient")
    {
        return RadioStationDialog.Show(owner, activeCategoryId);
    }
}
