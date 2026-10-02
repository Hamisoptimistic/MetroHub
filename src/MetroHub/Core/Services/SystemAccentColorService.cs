using System;
using System.Windows;
using System.Windows.Media;
using MetroHub.Presentation.Themes;

namespace MetroHub.Core.Services;

/// <summary>
/// Service that dynamically detects and synchronizes the application's accent colors with the Windows system accent color.
/// Automatically updates Application.Current.Resources and listens for OS theme/accent changes in real-time.
/// </summary>
public static class SystemAccentColorService
{
    private static Windows.UI.ViewManagement.UISettings? _uiSettings;
    private static bool _initialized;
    private static readonly object _lock = new();

    public static Color CurrentAccentColor { get; private set; } = Color.FromRgb(0x4C, 0xC2, 0xFF);
    public static string CurrentAccentHex { get; private set; } = "#4CC2FF";

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

    private static void ApplyToResources(Color accent, Color secondary, Color tertiary)
    {
        var app = Application.Current;
        if (app == null) return;

        var res = app.Resources;

        // Core Accent Color & Brushes
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorBrush"] = CreateFrozenBrush(accent);
        res["SystemAccentColorPrimaryBrush"] = CreateFrozenBrush(accent);
        res["SystemAccentColorSecondaryBrush"] = CreateFrozenBrush(secondary);
        res["SystemAccentColorTertiaryBrush"] = CreateFrozenBrush(tertiary);

        // Selection & Drag/Drop Fills
        res["SystemAccentColorSelectionFill"] = CreateFrozenBrush(accent, 0.15);
        res["SystemAccentColorDropSlotFill"] = CreateFrozenBrush(accent, 0.20);
        res["SystemAccentColorGroupDropFill"] = CreateFrozenBrush(accent, 0.08);

        // Fluent Primary Accent Button Interaction Brushes
        res["FluentPrimaryButtonBackgroundBrush"] = CreateFrozenBrush(accent, 0.15);
        res["FluentPrimaryButtonBorderBrush"] = CreateFrozenBrush(accent, 0.38);
        res["FluentPrimaryButtonHoverBackgroundBrush"] = CreateFrozenBrush(secondary, 0.22);
        res["FluentPrimaryButtonHoverBorderBrush"] = CreateFrozenBrush(secondary, 0.52);
        res["FluentPrimaryButtonPressedBackgroundBrush"] = CreateFrozenBrush(tertiary, 0.12);
        res["FluentPrimaryButtonPressedBorderBrush"] = CreateFrozenBrush(tertiary, 0.31);

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
