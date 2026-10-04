using System;
using System.Windows;
using System.Windows.Media;
using MetroHub.Core.Models;
using MetroHub.Presentation.Themes;

namespace MetroHub.Core.Services;

/// <summary>
/// Service that dynamically detects and synchronizes the application's accent colors with the Windows system accent color.
/// Supports artistic style transformations (such as Studio Glass luminous gradients) and listens for OS changes in real-time.
/// </summary>
public static class SystemAccentColorService
{
    private static Windows.UI.ViewManagement.UISettings? _uiSettings;
    private static bool _initialized;
    private static readonly object _lock = new();

    public static Color CurrentAccentColor { get; private set; } = Color.FromRgb(0x4C, 0xC2, 0xFF);
    public static string CurrentAccentHex { get; private set; } = "#4CC2FF";

    /// <summary>
    /// Current artistic accent rendering style (e.g. StudioGlass vs Flat). Defaults to Flat.
    /// </summary>
    public static AccentStyleMode CurrentStyleMode { get; private set; } = AccentStyleMode.Flat;

    /// <summary>
    /// Updates the active accent rendering style mode and dynamically repaints application resources.
    /// </summary>
    public static void SetStyleMode(AccentStyleMode mode)
    {
        if (CurrentStyleMode == mode && _initialized) return;
        CurrentStyleMode = mode;
        if (_initialized)
        {
            UpdateSystemAccentColors();
        }
    }

    /// <summary>
    /// Initializes system accent detection and registers dynamic change listeners.
    /// </summary>
    public static void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                _uiSettings = new Windows.UI.ViewManagement.UISettings();
                _uiSettings.ColorValuesChanged += (sender, args) =>
                {
                    Application.Current?.Dispatcher.InvokeAsync(UpdateSystemAccentColors);
                };
            }
            catch (Exception ex)
            {
                Safe.Log("SystemAccentColorService.Initialize.UISettings", ex);
                try
                {
                    Microsoft.Win32.SystemEvents.UserPreferenceChanged += (sender, e) =>
                    {
                        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color)
                        {
                            Application.Current?.Dispatcher.InvokeAsync(UpdateSystemAccentColors);
                        }
                    };
                }
                catch (Exception pEx)
                {
                    Safe.Log("SystemAccentColorService.Initialize.SystemEvents", pEx);
                }
            }

            UpdateSystemAccentColors();
        }
    }

    /// <summary>
    /// Queries the current Windows accent color and applies it to Application resources.
    /// </summary>
    public static void UpdateSystemAccentColors()
    {
        Color accentColor;
        Color accentSecondary;
        Color accentTertiary;

        try
        {
            if (_uiSettings != null)
            {
                var winAccent = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
                accentColor = Color.FromArgb(winAccent.A, winAccent.R, winAccent.G, winAccent.B);

                var winLight = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight1);
                accentSecondary = Color.FromArgb(winLight.A, winLight.R, winLight.G, winLight.B);

                var winDark = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentDark1);
                accentTertiary = Color.FromArgb(winDark.A, winDark.R, winDark.G, winDark.B);
            }
            else
            {
                accentColor = GetFallbackSystemAccentColor();
                accentSecondary = Lighten(accentColor, 0.15f);
                accentTertiary = Darken(accentColor, 0.15f);
            }
        }
        catch
        {
            accentColor = GetFallbackSystemAccentColor();
            accentSecondary = Lighten(accentColor, 0.15f);
            accentTertiary = Darken(accentColor, 0.15f);
        }

        CurrentAccentColor = accentColor;
        CurrentAccentHex = $"#{accentColor.R:X2}{accentColor.G:X2}{accentColor.B:X2}";

        ApplyToResources(accentColor, accentSecondary, accentTertiary);
    }

    private static Color GetFallbackSystemAccentColor()
    {
        try
        {
            Color glass = SystemParameters.WindowGlassColor;
            if (glass.A > 0 && (glass.R > 20 || glass.G > 20 || glass.B > 20))
            {
                return Color.FromRgb(glass.R, glass.G, glass.B);
            }
        }
        catch { }

        return Color.FromRgb(0x4C, 0xC2, 0xFF);
    }

    /// <summary>
    /// Creates the primary accent brush based on the active artistic style mode.
    /// </summary>
    public static Brush CreatePrimaryAccentBrush(Color accent, AccentStyleMode mode)
    {
        if (mode == AccentStyleMode.StudioGlass)
        {
            Color specularTop = Lighten(accent, 0.20f);
            var brush = new LinearGradientBrush(specularTop, accent, new Point(0, 0), new Point(0, 1));
            brush.Freeze();
            return brush;
        }

        return CreateFrozenBrush(accent);
    }

    /// <summary>
    /// Creates the secondary (hover) accent brush based on the active artistic style mode.
    /// </summary>
    public static Brush CreateSecondaryAccentBrush(Color secondary, AccentStyleMode mode)
    {
        if (mode == AccentStyleMode.StudioGlass)
        {
            Color specularTop = Lighten(secondary, 0.22f);
            var brush = new LinearGradientBrush(specularTop, secondary, new Point(0, 0), new Point(0, 1));
            brush.Freeze();
            return brush;
        }

        return CreateFrozenBrush(secondary);
    }

    /// <summary>
    /// Creates the tertiary (pressed/active) accent brush based on the active artistic style mode.
    /// </summary>
    public static Brush CreateTertiaryAccentBrush(Color accent, Color tertiary, AccentStyleMode mode)
    {
        if (mode == AccentStyleMode.StudioGlass)
        {
            var brush = new LinearGradientBrush(accent, tertiary, new Point(0, 0), new Point(0, 1));
            brush.Freeze();
            return brush;
        }

        return CreateFrozenBrush(tertiary);
    }

    /// <summary>
    /// Creates a specular top-lit glass edge border brush (or solid accent in Flat mode).
    /// </summary>
    public static Brush CreateGlassBorderBrush(Color accent, AccentStyleMode mode = AccentStyleMode.Flat)
    {
        if (mode == AccentStyleMode.StudioGlass)
        {
            Color topEdge = Lighten(accent, 0.35f);
            var brush = new LinearGradientBrush(topEdge, accent, new Point(0, 0), new Point(0, 1));
            brush.Freeze();
            return brush;
        }

        return CreateFrozenBrush(accent);
    }

    private static void ApplyToResources(Color accent, Color secondary, Color tertiary)
    {
        var app = Application.Current;
        if (app == null) return;

        var res = app.Resources;

        var primaryBrush = CreatePrimaryAccentBrush(accent, CurrentStyleMode);
        var secondaryBrush = CreateSecondaryAccentBrush(secondary, CurrentStyleMode);
        var tertiaryBrush = CreateTertiaryAccentBrush(accent, tertiary, CurrentStyleMode);
        var glassBorderBrush = CreateGlassBorderBrush(accent, CurrentStyleMode);

        // Core Accent Color & Brushes
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorBrush"] = primaryBrush;
        res["SystemAccentColorPrimaryBrush"] = primaryBrush;
        res["SystemAccentColorSecondaryBrush"] = secondaryBrush;
        res["SystemAccentColorTertiaryBrush"] = tertiaryBrush;
        res["SystemAccentGlassBorderBrush"] = glassBorderBrush;
        res["SystemAccentColorForegroundBrush"] = CreateFrozenBrush(GetContrastingForeground(accent));

        // Selection & Drag/Drop Fills
        res["SystemAccentColorSelectionFill"] = CreateFrozenBrush(accent, 0.15);
        res["SystemAccentColorDropSlotFill"] = CreateFrozenBrush(accent, 0.20);
        res["SystemAccentColorGroupDropFill"] = CreateFrozenBrush(accent, 0.08);

        // Universal Fluent ToggleSwitch Active Track Brushes
        // NOTE: The third-party WPF-UI library (Wpf.Ui.dll) specifically looks up these exact string keys
        // in its internal ToggleSwitch control template. Setting them here dynamically repaints the toggle switch track.
        res["ToggleSwitchFillOn"] = primaryBrush;
        res["ToggleSwitchFillOnPointerOver"] = secondaryBrush;
        res["ToggleSwitchFillOnPressed"] = tertiaryBrush;
        res["ToggleSwitchFillOnDisabled"] = CreateFrozenBrush(accent, 0.5);
        res["ToggleSwitchStrokeOn"] = primaryBrush;
        res["ToggleSwitchStrokeOnPointerOver"] = secondaryBrush;
        res["ToggleSwitchStrokeOnPressed"] = tertiaryBrush;

        // Synchronize ThemeTokens C# cache
        ThemeTokens.AccentPrimaryColor = accent;
        ThemeTokens.AccentSecondaryColor = secondary;
        ThemeTokens.AccentPrimaryBrush = CreateFrozenBrush(accent);
    }

    private static SolidColorBrush CreateFrozenBrush(Color c, double opacity = 1.0)
    {
        var brush = new SolidColorBrush(c) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static Color GetContrastingForeground(Color c)
    {
        // WCAG standard relative luminance calculation for high-contrast legible text
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance > 0.65 ? Colors.Black : Colors.White;
    }

    private static Color Lighten(Color color, float fraction)
    {
        byte r = (byte)Math.Clamp(color.R + (255 - color.R) * fraction, 0, 255);
        byte g = (byte)Math.Clamp(color.G + (255 - color.G) * fraction, 0, 255);
        byte b = (byte)Math.Clamp(color.B + (255 - color.B) * fraction, 0, 255);
        return Color.FromRgb(r, g, b);
    }

    private static Color Darken(Color color, float fraction)
    {
        byte r = (byte)Math.Clamp(color.R * (1 - fraction), 0, 255);
        byte g = (byte)Math.Clamp(color.G * (1 - fraction), 0, 255);
        byte b = (byte)Math.Clamp(color.B * (1 - fraction), 0, 255);
        return Color.FromRgb(r, g, b);
    }
}
