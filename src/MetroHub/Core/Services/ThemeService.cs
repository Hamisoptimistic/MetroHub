using System.Diagnostics;
using System.Windows;
using MetroHub.Core.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MetroHub.Core.Services;

public enum AppTheme
{
    Dark,
    Light
}

public static class ThemeService
{
    private static AppTheme _currentTheme = AppTheme.Dark;
    private static AppSettings? _settings;

    public static AppTheme CurrentTheme => _currentTheme;
    public static bool IsDarkTheme => _currentTheme == AppTheme.Dark;

    public static event EventHandler<AppTheme>? ThemeChanged;

    public static void Initialize(AppSettings settings)
    {
        _settings = settings;

        bool isDark = true;
        if (string.Equals(settings.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase))
        {
            isDark = false;
        }
        else if (string.Equals(settings.ThemeMode, "System", StringComparison.OrdinalIgnoreCase))
        {
            isDark = IsSystemDarkTheme();
        }
        else
        {
            isDark = true;
        }

        ApplyTheme(isDark ? AppTheme.Dark : AppTheme.Light, saveSetting: false);
    }

    public static bool IsSystemDarkTheme()
    {
        try
        {
            return ApplicationThemeManager.GetSystemTheme() != SystemTheme.Light;
        }
        catch
        {
            return true;
        }
    }

    public static void Toggle()
    {
        var targetTheme = IsDarkTheme ? AppTheme.Light : AppTheme.Dark;
        ApplyTheme(targetTheme, saveSetting: true);
    }

    public static void ApplyTheme(AppTheme theme, bool saveSetting = true)
    {
        _currentTheme = theme;
        bool isDark = theme == AppTheme.Dark;

        // 1. Swap active theme ResourceDictionary in Application MergedDictionaries
        try
        {
            var targetUri = new Uri(
                isDark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml",
                UriKind.Relative);

            var newDict = new ResourceDictionary { Source = targetUri };

            var appResources = Application.Current?.Resources;
            if (appResources != null)
            {
                bool replaced = false;
                for (int i = 0; i < appResources.MergedDictionaries.Count; i++)
                {
                    var md = appResources.MergedDictionaries[i];
                    if (md.Source != null && (md.Source.OriginalString.Contains("DarkTheme.xaml") || md.Source.OriginalString.Contains("LightTheme.xaml")))
                    {
                        appResources.MergedDictionaries[i] = newDict;
                        replaced = true;
                        break;
                    }
                }

                if (!replaced)
                {
                    appResources.MergedDictionaries.Add(newDict);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeService] ResourceDictionary swap failed: {ex.Message}");
        }

        // 2. Apply WPF-UI theme
        try
        {
            ApplicationThemeManager.Apply(
                isDark ? ApplicationTheme.Dark : ApplicationTheme.Light,
                WindowBackdropType.None,
                updateAccent: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeService] ApplicationThemeManager.Apply failed: {ex.Message}");
        }

        // 3. Persist setting if requested
        if (saveSetting && _settings != null)
        {
            _settings.ThemeMode = isDark ? "Dark" : "Light";
            StorageService.SaveSettings(_settings);
        }

        // 4. Notify subscribers
        ThemeChanged?.Invoke(null, _currentTheme);
    }
}
