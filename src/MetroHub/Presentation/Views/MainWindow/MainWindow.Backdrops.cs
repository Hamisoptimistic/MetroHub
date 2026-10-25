using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
using MetroHub.Presentation.Themes;

namespace MetroHub;

public partial class MainWindow
{
    private BackdropManager? _backdropManager;
    public BackdropManager BackdropManager => _backdropManager ??= new BackdropManager(
        this,
        () => Settings,
        RootGrid,
        CustomWallpaperHost,
        WallpaperImage,
        WallpaperVideo,
        WallpaperScrim,
        WallpaperTranslateTransform,
        WallpaperVideoTranslateTransform,
        ContentScrollViewer,
        ShowToast,
        UpdateBackdropMenuChecks);

    private void OnBackdropMicaClick(object sender, RoutedEventArgs e)
    {
        Settings.BackdropType = "Mica";
        StorageService.SaveSettings(Settings);
        ApplyConfiguredBackdrop();
    }

    private void OnBackdropAcrylicClick(object sender, RoutedEventArgs e)
    {
        Settings.BackdropType = "Acrylic";
        StorageService.SaveSettings(Settings);
        ApplyConfiguredBackdrop();
    }

    private void OnBackdropDesktopWallpaperClick(object sender, RoutedEventArgs e)
    {
        Settings.BackdropType = "DesktopWallpaper";
        StorageService.SaveSettings(Settings);
        ApplyConfiguredBackdrop();
    }

    private void OnBackdropBingDailyClick(object sender, RoutedEventArgs e)
    {
        Settings.BackdropType = "BingDaily";
        StorageService.SaveSettings(Settings);
        ApplyConfiguredBackdrop();
    }

    private void OnBackdropSpotlightDailyClick(object sender, RoutedEventArgs e)
    {
        Settings.BackdropType = "SpotlightDaily";
        StorageService.SaveSettings(Settings);
        ApplyConfiguredBackdrop();
    }

    private void OnBackdropCustomImageClick(object sender, RoutedEventArgs e)
    {
        IsDialogOpen = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Custom Wallpaper (Image or Video)",
                Filter = "All Supported Wallpapers (*.jpg;*.png;*.bmp;*.webp;*.mp4;*.webm;*.mkv;*.mov)|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.mp4;*.webm;*.mkv;*.mov|Image Files (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp|Video Files (*.mp4;*.webm;*.mkv;*.mov)|*.mp4;*.webm;*.mkv;*.mov|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                Settings.BackdropType = "Wallpaper";
                Settings.CustomWallpaperPath = dialog.FileName;
                StorageService.SaveSettings(Settings);
                ApplyConfiguredBackdrop();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to open custom wallpaper dialog: {ex.Message}");
        }
        finally
        {
            IsDialogOpen = false;
            Activate();
        }
    }

    private void OnWallpaperDimLightClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.50);
    private void OnWallpaperDimBalancedClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.70);
    private void OnWallpaperDimHeavyClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.85);

    private void SetWallpaperDim(double opacity) => BackdropManager.SetWallpaperDim(opacity);

    private void OnWallpaperEffectNoneClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("None");
    private void OnWallpaperEffectBlurClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("Blur");
    private void OnWallpaperEffectWaterRippleClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("WaterRipple");
    private void OnWallpaperEffectWaterCausticsClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("WaterCaustics");
    private void OnWallpaperEffectNeonClockClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("NeonClock");
    private void OnWallpaperEffectFluidWavesClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("FluidWaves");
    private void OnWallpaperEffectStarfieldClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("Starfield");
    private void OnWallpaperEffectCircuitClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("Circuit");
    private void OnWallpaperEffectEtherClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("Ether");
    private void OnWallpaperEffectSmokeFogClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("SmokeFog");
    private void OnWallpaperEffectKalisetClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("Kaliset");

    private void SetWallpaperEffect(string effect)
    {
        Settings.WallpaperEffect = effect;
        StorageService.SaveSettings(Settings);
        UpdateBackdropMenuChecks();
        ApplyConfiguredBackdrop();
    }

    private void OnParallaxEnabledClick(object sender, RoutedEventArgs e)
    {
        Settings.WallpaperParallax = true;
        StorageService.SaveSettings(Settings);
        UpdateBackdropMenuChecks();
        UpdateWallpaperParallax();
    }

    private void OnParallaxDisabledClick(object sender, RoutedEventArgs e)
    {
        Settings.WallpaperParallax = false;
        StorageService.SaveSettings(Settings);
        UpdateBackdropMenuChecks();
        UpdateWallpaperParallax();
    }

    public void ApplyConfiguredBackdrop(bool force = true) => BackdropManager.ApplyConfiguredBackdrop(force);

    public static bool IsWallpaperBackdrop(string? backdropType) => BackdropManager.IsWallpaperBackdrop(backdropType);

    public static BitmapImage? LoadOptimizedBitmap(string path, int decodeWidth = 1920) => BackdropManager.LoadOptimizedBitmap(path, decodeWidth);

    public static string? GetActiveDesktopWallpaperPath() => BackdropManager.GetActiveDesktopWallpaperPath();

    public void CancelPendingWallpaperLoad() => BackdropManager.CancelPendingWallpaperLoad();

    private void OnWallpaperVideoMediaOpened(object? sender, RoutedEventArgs e)
    {
        if (WallpaperVideo == null) return;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(250));
        WallpaperVideo.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void OnWallpaperVideoMediaEnded(object? sender, RoutedEventArgs e)
    {
        if (WallpaperVideo == null) return;
        try
        {
            WallpaperVideo.Position = TimeSpan.Zero;
            WallpaperVideo.Play();
        }
        catch { }
    }

    private void OnWallpaperVideoMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        string failedPath = BackdropManager.CurrentLoadedWallpaperPath ?? string.Empty;
        TeardownWallpaperVideo();
        ShowToast(BackdropManager.GetMissingCodecMessage(failedPath), isError: true);
    }

    public void ShowToast(string message, bool isError = false)
    {
        // Notification toast UI has been removed from MainWindow. Preserving signature for callers.
    }

    private void OnContentScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateWallpaperParallax();
        if (Math.Abs(e.VerticalChange) > 0.05 || Math.Abs(e.HorizontalChange) > 0.05)
        {
            Presentation.Controls.TileControl.SuppressRevealForScrolling();
        }
    }

    public void UpdateWallpaperParallax() => BackdropManager.UpdateWallpaperParallax();

    public void TeardownWallpaperVideo() => BackdropManager.TeardownWallpaperVideo();

    private void UpdateBackdropMenuChecks()
    {
        if (BackdropMicaItem != null)
            BackdropMicaItem.IsChecked = string.Equals(Settings.BackdropType, "Mica", StringComparison.OrdinalIgnoreCase);
        if (BackdropAcrylicItem != null)
            BackdropAcrylicItem.IsChecked = string.Equals(Settings.BackdropType, "Acrylic", StringComparison.OrdinalIgnoreCase);
        if (BackdropDesktopWallpaperItem != null)
            BackdropDesktopWallpaperItem.IsChecked = string.Equals(Settings.BackdropType, "DesktopWallpaper", StringComparison.OrdinalIgnoreCase);
        if (BackdropBingDailyItem != null)
            BackdropBingDailyItem.IsChecked = string.Equals(Settings.BackdropType, "BingDaily", StringComparison.OrdinalIgnoreCase);
        if (BackdropSpotlightDailyItem != null)
            BackdropSpotlightDailyItem.IsChecked = string.Equals(Settings.BackdropType, "SpotlightDaily", StringComparison.OrdinalIgnoreCase);
        if (BackdropCustomImageItem != null)
            BackdropCustomImageItem.IsChecked = string.Equals(Settings.BackdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase);

        double dim = Settings.WallpaperDimOpacity;
        if (DimLightItem != null) DimLightItem.IsChecked = Math.Abs(dim - 0.50) < 0.05;
        if (DimBalancedItem != null) DimBalancedItem.IsChecked = Math.Abs(dim - 0.70) < 0.05;
        if (DimHeavyItem != null) DimHeavyItem.IsChecked = Math.Abs(dim - 0.85) < 0.05;

        bool isWallpaper = IsWallpaperBackdrop(Settings.BackdropType);

        if (ParallaxEnabledItem != null)
        {
            ParallaxEnabledItem.IsChecked = Settings.WallpaperParallax;
            ParallaxEnabledItem.IsEnabled = isWallpaper;
        }
        if (ParallaxDisabledItem != null)
        {
            ParallaxDisabledItem.IsChecked = !Settings.WallpaperParallax;
            ParallaxDisabledItem.IsEnabled = isWallpaper;
        }

        string currentEffect = Settings.WallpaperEffect ?? "None";
        if (EffectNoneItem != null) EffectNoneItem.IsChecked = string.Equals(currentEffect, "None", StringComparison.OrdinalIgnoreCase);
        if (EffectBlurItem != null) EffectBlurItem.IsChecked = string.Equals(currentEffect, "Blur", StringComparison.OrdinalIgnoreCase);
        if (EffectWaterRippleItem != null) EffectWaterRippleItem.IsChecked = string.Equals(currentEffect, "WaterRipple", StringComparison.OrdinalIgnoreCase);
        if (EffectWaterCausticsItem != null) EffectWaterCausticsItem.IsChecked = string.Equals(currentEffect, "WaterCaustics", StringComparison.OrdinalIgnoreCase);
        if (EffectNeonClockItem != null) EffectNeonClockItem.IsChecked = string.Equals(currentEffect, "NeonClock", StringComparison.OrdinalIgnoreCase);
        if (EffectFluidWavesItem != null) EffectFluidWavesItem.IsChecked = string.Equals(currentEffect, "FluidWaves", StringComparison.OrdinalIgnoreCase);
        if (EffectStarfieldItem != null) EffectStarfieldItem.IsChecked = string.Equals(currentEffect, "Starfield", StringComparison.OrdinalIgnoreCase);
        if (EffectCircuitItem != null) EffectCircuitItem.IsChecked = string.Equals(currentEffect, "Circuit", StringComparison.OrdinalIgnoreCase);
        if (EffectEtherItem != null) EffectEtherItem.IsChecked = string.Equals(currentEffect, "Ether", StringComparison.OrdinalIgnoreCase);
        if (EffectSmokeFogItem != null) EffectSmokeFogItem.IsChecked = string.Equals(currentEffect, "SmokeFog", StringComparison.OrdinalIgnoreCase);
        if (EffectKalisetItem != null) EffectKalisetItem.IsChecked = string.Equals(currentEffect, "Kaliset", StringComparison.OrdinalIgnoreCase);

        if (WallpaperEffectsMenu != null) WallpaperEffectsMenu.IsEnabled = isWallpaper;
    }
}
