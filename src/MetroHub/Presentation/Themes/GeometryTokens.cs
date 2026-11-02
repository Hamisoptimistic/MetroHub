using System.Windows;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Themes;

/// <summary>
/// Centralized manager for universal geometry tokens (CornerRadii, Thickness, and UI Scale) matching Tokens.xaml.
/// Synchronizes global resource dictionary tokens with <see cref="SettingsService"/> to provide a single authoritative write-path.
/// </summary>
public static class GeometryTokens
{
    private static bool _initialized;
    private static readonly object _lock = new();

    public static CornerRadius TileCornerRadius { get; private set; } = new(2);
    public static CornerRadius ControlCornerRadius { get; private set; } = new(2);
    public static CornerRadius ControlCornerRadiusBottom { get; private set; } = new(0, 0, 2, 2);
    public static CornerRadius DialogCornerRadius { get; private set; } = new(0);
    public static double AppUiScale { get; private set; } = 1.0;

    /// <summary>
    /// Initializes geometry tokens from current application settings and subscribes to change notifications.
    /// </summary>
    public static void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;

            ApplyCornerRadius(SettingsService.Instance.Current.TileCornerRadius);

            SettingsService.Instance.SettingChanged += prop =>
            {
                if (prop == nameof(AppSettings.TileCornerRadius))
                {
                    var app = Application.Current;
                    if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
                    {
                        app.Dispatcher.InvokeAsync(() => ApplyCornerRadius(SettingsService.Instance.Current.TileCornerRadius));
                    }
                    else
                    {
                        ApplyCornerRadius(SettingsService.Instance.Current.TileCornerRadius);
                    }
                }
            };
        }
    }

    /// <summary>
    /// Updates the global corner radius tokens in <see cref="Application.Current.Resources"/> based on the base tile radius.
    /// </summary>
    /// <param name="radius">The base radius in pixels (0 = Sharp, 2 = Subtle, 4 = Medium, 8 = Rounded).</param>
    public static void ApplyCornerRadius(int radius)
    {
        var app = Application.Current;
        if (app == null) return;

        var res = app.Resources;
        var tileRadius = new CornerRadius(radius);
        var controlRadius = new CornerRadius(radius <= 2 ? radius : (radius == 4 ? 4 : 6));
        var bottomRadius = new CornerRadius(0, 0, radius, radius);
        var dialogRadius = new CornerRadius(0); // Strict Sharp Metro: Settings & Dialogs are 0px

        res["TileCornerRadius"] = tileRadius;
        res["ControlCornerRadius"] = controlRadius;
        res["ControlCornerRadiusBottom"] = bottomRadius;
        res["DialogCornerRadius"] = dialogRadius;

        TileCornerRadius = tileRadius;
        ControlCornerRadius = controlRadius;
        ControlCornerRadiusBottom = bottomRadius;
        DialogCornerRadius = dialogRadius;
    }

    /// <summary>
    /// Updates the application-wide UI scale factor token in <see cref="Application.Current.Resources"/>.
    /// </summary>
    /// <param name="scale">The scale multiplier (e.g. 1.0 = 100%).</param>
    public static void ApplyUiScale(double scale)
    {
        var app = Application.Current;
        if (app == null) return;

        app.Resources["AppUiScale"] = scale;
        AppUiScale = scale;
    }
}
