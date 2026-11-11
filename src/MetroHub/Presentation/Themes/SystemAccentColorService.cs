using System;
using System.Windows;
using System.Windows.Media;
using MetroHub.Core.Common;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using Wpf.Ui.Appearance;

namespace MetroHub.Presentation.Themes;

/// <summary>
/// Service that dynamically detects and synchronizes the application's accent colors with the Windows system accent color.
/// Supports artistic style transformations (such as Studio Glass luminous gradients) and listens for OS changes in real-time.
/// </summary>
public static class SystemAccentColorService
{
    private static Windows.UI.ViewManagement.UISettings? _uiSettings;
    private static bool _initialized;
    private static readonly Lock _lock = new();

    public static Color CurrentAccentColor { get; private set; } = Color.FromRgb(0x4C, 0xC2, 0xFF);
    public static string CurrentAccentHex { get; private set; } = "#4CC2FF";
    public static Color RawAccentColor { get; private set; } = Color.FromRgb(0x00, 0x78, 0xD4);

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
        ColorExtractorService.FallbackAccentHexProvider = () => CurrentAccentHex;

        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                _uiSettings = new Windows.UI.ViewManagement.UISettings();
                _uiSettings.ColorValuesChanged += (sender, args) =>
                {
                    _ = Application.Current?.Dispatcher.InvokeAsync(UpdateSystemAccentColors);
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
                            _ = Application.Current?.Dispatcher.InvokeAsync(UpdateSystemAccentColors);
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
    /// In Dark Mode, Fluent controls (Quick Settings, sliders, toggles) use AccentLight2
    /// for primary fills to maintain clean contrast and aesthetic presentation against dark backplates.
    /// </summary>
    public static void UpdateSystemAccentColors()
    {
        Color accentColor = GetFallbackSystemAccentColor();
        Color accentSecondary = Lighten(accentColor, 0.15f);
        Color accentTertiary = Darken(accentColor, 0.15f);
        Color rawAccent = Darken(accentColor, 0.25f);

        if (_uiSettings != null)
        {
            try
            {
                // In Windows 11 Dark Mode, Fluent controls use:
                // Primary: AccentLight2 (resting state fill, matching Windows Quick Settings / Sliders)
                // Secondary: AccentLight3 (pointer over / hover)
                // Tertiary: AccentLight1 (pressed / active)
                var winAccentLight2 = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight2);
                var winAccentLight3 = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight3);
                var winAccentLight1 = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight1);
                var winRawAccent = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);

                accentColor = Color.FromArgb(winAccentLight2.A, winAccentLight2.R, winAccentLight2.G, winAccentLight2.B);
                accentSecondary = Color.FromArgb(winAccentLight3.A, winAccentLight3.R, winAccentLight3.G, winAccentLight3.B);
                accentTertiary = Color.FromArgb(winAccentLight1.A, winAccentLight1.R, winAccentLight1.G, winAccentLight1.B);
                rawAccent = Color.FromArgb(winRawAccent.A, winRawAccent.R, winRawAccent.G, winRawAccent.B);
            }
            catch (Exception ex)
            {
                Safe.Log("SystemAccentColorService.UpdateSystemAccentColors", ex);
            }
        }

        CurrentAccentColor = accentColor;
        CurrentAccentHex = $"#{accentColor.R:X2}{accentColor.G:X2}{accentColor.B:X2}";
        RawAccentColor = rawAccent;

        ApplyToResources(accentColor, accentSecondary, accentTertiary, rawAccent);
    }

    private static Color GetFallbackSystemAccentColor()
    {
        try
        {
            Color glass = SystemParameters.WindowGlassColor;
            if (glass.A > 0 && (glass.R > 20 || glass.G > 20 || glass.B > 20))
            {
                return Lighten(Color.FromRgb(glass.R, glass.G, glass.B), 0.25f);
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

    private static void ApplyToResources(Color accent, Color secondary, Color tertiary, Color rawAccent)
    {
        var app = Application.Current;
        if (app == null) return;

        // Synchronize all WPF-UI native controls (ListViewItem, ToggleSwitch, ComboBox, etc.) via official API
        try
        {
            ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark);
        }
        catch (Exception ex)
        {
            Safe.Log("SystemAccentColorService.ApplyToResources.WpfUi", ex);
        }

        var res = app.Resources;

        var primaryBrush = CreatePrimaryAccentBrush(accent, CurrentStyleMode);
        var secondaryBrush = CreateSecondaryAccentBrush(secondary, CurrentStyleMode);
        var tertiaryBrush = CreateTertiaryAccentBrush(accent, tertiary, CurrentStyleMode);
        var glassBorderBrush = CreateGlassBorderBrush(accent, CurrentStyleMode);

        // Core Accent Color & Brushes (MetroHub tokens)
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorBrush"] = primaryBrush;
        res["SystemAccentColorPrimaryBrush"] = primaryBrush;
        res["SystemAccentColorSolidBrush"] = CreateFrozenBrush(accent);
        res["SystemAccentColorSecondaryBrush"] = secondaryBrush;
        res["SystemAccentColorTertiaryBrush"] = tertiaryBrush;
        res["SystemAccentGlassBorderBrush"] = glassBorderBrush;
        res["SystemAccentColorForegroundBrush"] = CreateFrozenBrush(GetContrastingForeground(accent));

        // Raw System Accent (base un-tinted Windows accent)
        res["SystemAccentColorRaw"] = rawAccent;
        res["SystemAccentColorRawBrush"] = CreateFrozenBrush(rawAccent);

        // Selection & Drag/Drop Fills
        res["SystemAccentColorSelectionFill"] = CreateFrozenBrush(accent, 0.15);
        res["SystemAccentColorDropSlotFill"] = CreateFrozenBrush(accent, 0.20);
        res["SystemAccentColorGroupDropFill"] = CreateFrozenBrush(accent, 0.08);

        // Control Accent Tokens (ToggleSwitch, ListViewItem, ComboBox)
        res["ToggleSwitchFillOn"] = primaryBrush;
        res["ToggleSwitchFillOnPointerOver"] = secondaryBrush;
        res["ToggleSwitchFillOnPressed"] = tertiaryBrush;
        res["ToggleSwitchFillOnDisabled"] = CreateFrozenBrush(accent, 0.5);
        res["ToggleSwitchStrokeOn"] = primaryBrush;
        res["ToggleSwitchStrokeOnPointerOver"] = secondaryBrush;
        res["ToggleSwitchStrokeOnPressed"] = tertiaryBrush;
        res["ListViewItemPillFillBrush"] = primaryBrush;
        res["ComboBoxItemPillFillBrush"] = primaryBrush;

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
