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
        UpdateBackdropMenuChecks,
        ShaderHost);

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
                Title = "Select Custom Picture",
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files (*.*)|*.*"
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
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to open custom picture dialog: {ex.Message}");
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

    private void OnImageEffectNoneClick(object sender, RoutedEventArgs e) => SetImageEffect("None");
    private void OnImageEffectBlurClick(object sender, RoutedEventArgs e) => SetImageEffect("Blur");
    private void OnImageEffectWaterRippleClick(object sender, RoutedEventArgs e) => SetImageEffect("WaterRipple");

    private void SetImageEffect(string effect)
    {
        Settings.ImageEffect = effect;
        StorageService.SaveSettings(Settings);
        UpdateBackdropMenuChecks();
        ApplyConfiguredBackdrop();
    }

    private void SetLiveWallpaper(string shaderName)
    {
        Settings.BackdropType = "LiveShader";
        Settings.WallpaperEffect = shaderName;
        StorageService.SaveSettings(Settings);
        UpdateBackdropMenuChecks();
        ApplyConfiguredBackdrop();
    }

    private void OnLiveStarfieldClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Starfield");
    private void OnLiveCyberTunnelClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("CyberTunnel");
    private void OnLiveFluidWavesClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("FluidWaves");
    private void OnLiveNeonClockClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("NeonClock");
    private void OnLiveCircuitClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Circuit");
    private void OnLiveEtherClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Ether");
    private void OnLiveSmokeFogClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("SmokeFog");
    private void OnLiveKalisetClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Kaliset");
    private void OnLiveKoiPondClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("KoiPond");
    private void OnLiveInkClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Ink");
    private void OnLiveWaterCausticsClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("WaterCaustics");
    private void OnLiveLagoonClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Lagoon");
    private void OnLivePlasmaClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Plasma");
    private void OnLiveAuroraClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Aurora");
    private void OnLivePebblesRoseClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Pebbles_Rose");
    private void OnLivePebblesAmberClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Pebbles_Amber");
    private void OnLivePebblesIceClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Pebbles_Ice");
    private void OnLivePebblesObsidianClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Pebbles_Obsidian");
    private void OnLivePebblesRerankClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Pebbles_Rerank");
    private void OnLivePebblesTranslateClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Pebbles_Translate");
    private void OnLiveSpectrumNorthSmallClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Spectrum_NorthSmall");
    private void OnLiveSpectrumPrismClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Spectrum_Prism");
    private void OnLiveSpectrumSunsetClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Spectrum_Sunset");
    private void OnLiveMeadowDynamicClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Meadow_Dynamic");
    private void OnLiveMeadowSummerClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Meadow_Summer");
    private void OnLiveMeadowCottageClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Meadow_Cottage");
    private void OnLiveMeadowPaleBlueClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Meadow_PaleBlue");
    private void OnLiveMeadowLonelyClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Meadow_Lonely");
    private void OnLiveMeadowNightClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Meadow_Night");
    private void OnLiveMeadowMonoClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Meadow_Mono");
    private void OnLiveEarthRealtimeClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Earth_Realtime");
    private void OnLiveEarthCinematicClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Earth_Cinematic");
    private void OnLiveMarsClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Mars");
    private void OnLiveJupiterClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Jupiter");
    private void OnLiveVenusClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Venus");
    private void OnLiveMercuryClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Mercury");
    private void OnLiveMoonClick(object sender, RoutedEventArgs e) => SetLiveWallpaper("Moon");

    private void OnLiveCustomVideoClick(object sender, RoutedEventArgs e)
    {
        IsDialogOpen = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Custom Video",
                Filter = "Video Files (*.mp4;*.webm;*.mkv;*.mov)|*.mp4;*.webm;*.mkv;*.mov|All Files (*.*)|*.*"
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
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to open custom video dialog: {ex.Message}");
        }
        finally
        {
            IsDialogOpen = false;
            Activate();
        }
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

        bool isStatic = BackdropManager.IsStaticWallpaper(Settings.BackdropType);
        bool isLive = BackdropManager.IsLiveShader(Settings.BackdropType);
        bool isVideo = string.Equals(Settings.BackdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase) &&
                       DailyWallpaperService.IsVideoWallpaper(Settings.CustomWallpaperPath);

        if (BackdropDesktopWallpaperItem != null)
            BackdropDesktopWallpaperItem.IsChecked = string.Equals(Settings.BackdropType, "DesktopWallpaper", StringComparison.OrdinalIgnoreCase);
        if (BackdropBingDailyItem != null)
            BackdropBingDailyItem.IsChecked = string.Equals(Settings.BackdropType, "BingDaily", StringComparison.OrdinalIgnoreCase);
        if (BackdropSpotlightDailyItem != null)
            BackdropSpotlightDailyItem.IsChecked = string.Equals(Settings.BackdropType, "SpotlightDaily", StringComparison.OrdinalIgnoreCase);
        if (BackdropCustomImageItem != null)
            BackdropCustomImageItem.IsChecked = isStatic && !isVideo && string.Equals(Settings.BackdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase);

        // Image Effects submenu: selectable ONLY when a static wallpaper is active
        if (ImageEffectsMenu != null)
        {
            ImageEffectsMenu.IsEnabled = isStatic && !isVideo;
        }

        string currentImageEffect = Settings.ImageEffect ?? "None";
        if (ImageEffectNoneItem != null)
            ImageEffectNoneItem.IsChecked = isStatic && string.Equals(currentImageEffect, "None", StringComparison.OrdinalIgnoreCase);
        if (ImageEffectBlurItem != null)
            ImageEffectBlurItem.IsChecked = isStatic && string.Equals(currentImageEffect, "Blur", StringComparison.OrdinalIgnoreCase);
        if (ImageEffectWaterRippleItem != null)
            ImageEffectWaterRippleItem.IsChecked = isStatic && string.Equals(currentImageEffect, "WaterRipple", StringComparison.OrdinalIgnoreCase);

        // Live Wallpapers
        string currentLiveShader = Settings.WallpaperEffect ?? "Starfield";
        if (LiveStarfieldItem != null) LiveStarfieldItem.IsChecked = isLive && string.Equals(currentLiveShader, "Starfield", StringComparison.OrdinalIgnoreCase);
        if (LiveCyberTunnelItem != null) LiveCyberTunnelItem.IsChecked = isLive && string.Equals(currentLiveShader, "CyberTunnel", StringComparison.OrdinalIgnoreCase);
        if (LiveFluidWavesItem != null) LiveFluidWavesItem.IsChecked = isLive && string.Equals(currentLiveShader, "FluidWaves", StringComparison.OrdinalIgnoreCase);
        if (LiveNeonClockItem != null) LiveNeonClockItem.IsChecked = isLive && string.Equals(currentLiveShader, "NeonClock", StringComparison.OrdinalIgnoreCase);
        if (LiveCircuitItem != null) LiveCircuitItem.IsChecked = isLive && string.Equals(currentLiveShader, "Circuit", StringComparison.OrdinalIgnoreCase);
        if (LiveEtherItem != null) LiveEtherItem.IsChecked = isLive && string.Equals(currentLiveShader, "Ether", StringComparison.OrdinalIgnoreCase);
        if (LiveSmokeFogItem != null) LiveSmokeFogItem.IsChecked = isLive && string.Equals(currentLiveShader, "SmokeFog", StringComparison.OrdinalIgnoreCase);
        if (LiveKalisetItem != null) LiveKalisetItem.IsChecked = isLive && string.Equals(currentLiveShader, "Kaliset", StringComparison.OrdinalIgnoreCase);
        if (LiveKoiPondItem != null) LiveKoiPondItem.IsChecked = isLive && string.Equals(currentLiveShader, "KoiPond", StringComparison.OrdinalIgnoreCase);
        if (LiveInkItem != null) LiveInkItem.IsChecked = isLive && string.Equals(currentLiveShader, "Ink", StringComparison.OrdinalIgnoreCase);
        if (LiveWaterCausticsItem != null) LiveWaterCausticsItem.IsChecked = isLive && string.Equals(currentLiveShader, "WaterCaustics", StringComparison.OrdinalIgnoreCase);
        if (LiveLagoonItem != null) LiveLagoonItem.IsChecked = isLive && string.Equals(currentLiveShader, "Lagoon", StringComparison.OrdinalIgnoreCase);
        if (LivePlasmaItem != null) LivePlasmaItem.IsChecked = isLive && string.Equals(currentLiveShader, "Plasma", StringComparison.OrdinalIgnoreCase);
        if (LiveAuroraItem != null) LiveAuroraItem.IsChecked = isLive && string.Equals(currentLiveShader, "Aurora", StringComparison.OrdinalIgnoreCase);

        if (LivePebblesRoseItem != null) LivePebblesRoseItem.IsChecked = isLive && (string.Equals(currentLiveShader, "Pebbles_Rose", StringComparison.OrdinalIgnoreCase) || string.Equals(currentLiveShader, "Pebbles", StringComparison.OrdinalIgnoreCase));
        if (LivePebblesAmberItem != null) LivePebblesAmberItem.IsChecked = isLive && string.Equals(currentLiveShader, "Pebbles_Amber", StringComparison.OrdinalIgnoreCase);
        if (LivePebblesIceItem != null) LivePebblesIceItem.IsChecked = isLive && string.Equals(currentLiveShader, "Pebbles_Ice", StringComparison.OrdinalIgnoreCase);
        if (LivePebblesObsidianItem != null) LivePebblesObsidianItem.IsChecked = isLive && string.Equals(currentLiveShader, "Pebbles_Obsidian", StringComparison.OrdinalIgnoreCase);
        if (LivePebblesRerankItem != null) LivePebblesRerankItem.IsChecked = isLive && string.Equals(currentLiveShader, "Pebbles_Rerank", StringComparison.OrdinalIgnoreCase);
        if (LivePebblesTranslateItem != null) LivePebblesTranslateItem.IsChecked = isLive && string.Equals(currentLiveShader, "Pebbles_Translate", StringComparison.OrdinalIgnoreCase);

        if (LiveSpectrumNorthSmallItem != null) LiveSpectrumNorthSmallItem.IsChecked = isLive && (string.Equals(currentLiveShader, "Spectrum_NorthSmall", StringComparison.OrdinalIgnoreCase) || string.Equals(currentLiveShader, "Spectrum", StringComparison.OrdinalIgnoreCase));
        if (LiveSpectrumPrismItem != null) LiveSpectrumPrismItem.IsChecked = isLive && string.Equals(currentLiveShader, "Spectrum_Prism", StringComparison.OrdinalIgnoreCase);
        if (LiveSpectrumSunsetItem != null) LiveSpectrumSunsetItem.IsChecked = isLive && string.Equals(currentLiveShader, "Spectrum_Sunset", StringComparison.OrdinalIgnoreCase);

        if (LiveMeadowDynamicItem != null) LiveMeadowDynamicItem.IsChecked = isLive && (string.Equals(currentLiveShader, "Meadow_Dynamic", StringComparison.OrdinalIgnoreCase) || string.Equals(currentLiveShader, "Meadow", StringComparison.OrdinalIgnoreCase));
        if (LiveMeadowSummerItem != null) LiveMeadowSummerItem.IsChecked = isLive && string.Equals(currentLiveShader, "Meadow_Summer", StringComparison.OrdinalIgnoreCase);
        if (LiveMeadowCottageItem != null) LiveMeadowCottageItem.IsChecked = isLive && string.Equals(currentLiveShader, "Meadow_Cottage", StringComparison.OrdinalIgnoreCase);
        if (LiveMeadowPaleBlueItem != null) LiveMeadowPaleBlueItem.IsChecked = isLive && string.Equals(currentLiveShader, "Meadow_PaleBlue", StringComparison.OrdinalIgnoreCase);
        if (LiveMeadowLonelyItem != null) LiveMeadowLonelyItem.IsChecked = isLive && string.Equals(currentLiveShader, "Meadow_Lonely", StringComparison.OrdinalIgnoreCase);
        if (LiveMeadowNightItem != null) LiveMeadowNightItem.IsChecked = isLive && string.Equals(currentLiveShader, "Meadow_Night", StringComparison.OrdinalIgnoreCase);
        if (LiveMeadowMonoItem != null) LiveMeadowMonoItem.IsChecked = isLive && (string.Equals(currentLiveShader, "Meadow_Mono", StringComparison.OrdinalIgnoreCase) || string.Equals(currentLiveShader, "Meadow_BlackAndWhite", StringComparison.OrdinalIgnoreCase));

        if (LiveEarthRealtimeItem != null) LiveEarthRealtimeItem.IsChecked = isLive && string.Equals(currentLiveShader, "Earth_Realtime", StringComparison.OrdinalIgnoreCase);
        if (LiveEarthCinematicItem != null) LiveEarthCinematicItem.IsChecked = isLive && (string.Equals(currentLiveShader, "Earth_Cinematic", StringComparison.OrdinalIgnoreCase) || string.Equals(currentLiveShader, "Earth", StringComparison.OrdinalIgnoreCase) || string.Equals(currentLiveShader, "Planet_Procedural", StringComparison.OrdinalIgnoreCase));
        if (LiveMarsItem != null) LiveMarsItem.IsChecked = isLive && string.Equals(currentLiveShader, "Mars", StringComparison.OrdinalIgnoreCase);
        if (LiveJupiterItem != null) LiveJupiterItem.IsChecked = isLive && string.Equals(currentLiveShader, "Jupiter", StringComparison.OrdinalIgnoreCase);
        if (LiveVenusItem != null) LiveVenusItem.IsChecked = isLive && string.Equals(currentLiveShader, "Venus", StringComparison.OrdinalIgnoreCase);
        if (LiveMercuryItem != null) LiveMercuryItem.IsChecked = isLive && string.Equals(currentLiveShader, "Mercury", StringComparison.OrdinalIgnoreCase);
        if (LiveMoonItem != null) LiveMoonItem.IsChecked = isLive && string.Equals(currentLiveShader, "Moon", StringComparison.OrdinalIgnoreCase);

        if (LiveCustomVideoItem != null) LiveCustomVideoItem.IsChecked = isVideo;

        // Dimming and Parallax
        double dim = Settings.WallpaperDimOpacity;
        if (DimLightItem != null) DimLightItem.IsChecked = Math.Abs(dim - 0.50) < 0.05;
        if (DimBalancedItem != null) DimBalancedItem.IsChecked = Math.Abs(dim - 0.70) < 0.05;
        if (DimHeavyItem != null) DimHeavyItem.IsChecked = Math.Abs(dim - 0.85) < 0.05;

        bool hasBackdropCanvas = isStatic || isLive;
        if (ParallaxEnabledItem != null)
        {
            ParallaxEnabledItem.IsChecked = Settings.WallpaperParallax;
            ParallaxEnabledItem.IsEnabled = hasBackdropCanvas;
        }
        if (ParallaxDisabledItem != null)
        {
            ParallaxDisabledItem.IsChecked = !Settings.WallpaperParallax;
            ParallaxDisabledItem.IsEnabled = hasBackdropCanvas;
        }
    }
}
