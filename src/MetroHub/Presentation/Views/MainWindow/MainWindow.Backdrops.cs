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
using MetroHub.Core.AsciiArt;
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
        ApplyAsciiScene,
        ApplyHalftone);

    private readonly List<(string Slug, MenuItem Item)> _liveSceneItems = [];
    private string? _currentSceneSlug;

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

    /// <summary>Builds the "Live Scenes" submenu from the .mhp packs on disk.</summary>
    private void BuildLiveScenesMenu()
    {
        if (BackdropLiveScenesItem == null || _liveSceneItems.Count > 0)
        {
            return;
        }

        IReadOnlyList<AsciiPackInfo> packs = AsciiPack.EnumerateAvailable();
        foreach (AsciiPackInfo pack in packs)
        {
            MenuItem item = new()
            {
                Header = pack.Name,
                Tag = pack.Slug,
                IsCheckable = true,
            };
            item.Click += OnBackdropLiveSceneClick;
            BackdropLiveScenesItem.Items.Add(item);
            _liveSceneItems.Add((pack.Slug, item));
        }

        BackdropLiveScenesItem.IsEnabled = _liveSceneItems.Count > 0;
    }

    private void OnBackdropLiveSceneClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string slug } || string.IsNullOrWhiteSpace(slug))
        {
            return;
        }

        Settings.BackdropType = "AsciiScene";
        Settings.AsciiSceneId = slug;
        StorageService.SaveSettings(Settings);
        ApplyConfiguredBackdrop();
    }

    /// <summary>Starts, keeps, or stops the native ASCII scene for the current backdrop.</summary>
    private void ApplyAsciiScene(string? slug)
    {
        if (!IsAsciiSceneBackdrop(Settings.BackdropType) || string.IsNullOrWhiteSpace(slug))
        {
            _currentSceneSlug = null;
            AsciiSceneWallpaper.Unload();
            AsciiSceneWallpaper.Visibility = Visibility.Collapsed;
            return;
        }

        if (string.Equals(_currentSceneSlug, slug, StringComparison.OrdinalIgnoreCase) && AsciiSceneWallpaper.HasScene)
        {
            AsciiSceneWallpaper.Visibility = Visibility.Visible;
            return;
        }

        string path = AsciiPack.GetPackPath(slug);
        if (!File.Exists(path))
        {
            _currentSceneSlug = null;
            AsciiSceneWallpaper.Unload();
            AsciiSceneWallpaper.Visibility = Visibility.Collapsed;
            ShowToast($"Scene pack not found: {slug}", isError: true);
            return;
        }

        try
        {
            AsciiSceneWallpaper.Load(path);
            AsciiSceneWallpaper.Visibility = Visibility.Visible;
            _currentSceneSlug = slug;
        }
        catch (Exception ex)
        {
            _currentSceneSlug = null;
            AsciiSceneWallpaper.Unload();
            AsciiSceneWallpaper.Visibility = Visibility.Collapsed;
            Serilog.Log.Warning(ex, "Failed to load ASCII scene {Slug}", slug);
            ShowToast($"Failed to load scene: {slug}", isError: true);
        }
    }

    private string? _currentHalftoneImagePath;
    private bool? _currentHalftoneIsAudioReactive;

    /// <summary>Starts, keeps, or stops the halftone wallpaper (live audio-reactive or static Honeycomb).</summary>
    private void ApplyHalftone(string? imagePath, bool isAudioReactive = true)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            if (_currentHalftoneImagePath != null)
            {
                _currentHalftoneImagePath = null;
                _currentHalftoneIsAudioReactive = null;
                AsciiSceneWallpaper.Unload();
                AsciiSceneWallpaper.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (string.Equals(_currentHalftoneImagePath, imagePath, StringComparison.OrdinalIgnoreCase) &&
            _currentHalftoneIsAudioReactive == isAudioReactive &&
            AsciiSceneWallpaper.HasScene)
        {
            AsciiSceneWallpaper.Visibility = Visibility.Visible;
            return;
        }

        if (!File.Exists(imagePath))
        {
            _currentHalftoneImagePath = null;
            _currentHalftoneIsAudioReactive = null;
            AsciiSceneWallpaper.Unload();
            AsciiSceneWallpaper.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            _currentSceneSlug = null;
            AsciiSceneWallpaper.LoadFromImage(imagePath, isAudioReactive);
            AsciiSceneWallpaper.Visibility = Visibility.Visible;
            _currentHalftoneImagePath = imagePath;
            _currentHalftoneIsAudioReactive = isAudioReactive;
        }
        catch (Exception ex)
        {
            _currentHalftoneImagePath = null;
            _currentHalftoneIsAudioReactive = null;
            AsciiSceneWallpaper.Unload();
            AsciiSceneWallpaper.Visibility = Visibility.Collapsed;
            Serilog.Log.Warning(ex, "Failed to load halftone wallpaper from {Path}", imagePath);
        }
    }

    private void OnWallpaperDimLightClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.50);
    private void OnWallpaperDimBalancedClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.70);
    private void OnWallpaperDimHeavyClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.85);

    private void SetWallpaperDim(double opacity) => BackdropManager.SetWallpaperDim(opacity);

    private void OnWallpaperEffectNoneClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("None");
    private void OnWallpaperEffectAudioHalftoneClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("AudioHalftone");
    private void OnWallpaperEffectHoneycombClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("Honeycomb");
    private void OnWallpaperEffectBlurClick(object sender, RoutedEventArgs e) => SetWallpaperEffect("Blur");

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

    public static bool IsAsciiSceneBackdrop(string? backdropType) => BackdropManager.IsAsciiSceneBackdrop(backdropType);

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

        bool isScene = IsAsciiSceneBackdrop(Settings.BackdropType);
        if (BackdropLiveScenesItem != null)
        {
            BackdropLiveScenesItem.IsEnabled = _liveSceneItems.Count > 0;
        }

        foreach ((string sceneSlug, MenuItem sceneItem) in _liveSceneItems)
        {
            sceneItem.IsChecked = isScene &&
                string.Equals(sceneSlug, Settings.AsciiSceneId, StringComparison.OrdinalIgnoreCase);
        }

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
        if (EffectAudioHalftoneItem != null) EffectAudioHalftoneItem.IsChecked = string.Equals(currentEffect, "AudioHalftone", StringComparison.OrdinalIgnoreCase);
        if (EffectHoneycombItem != null) EffectHoneycombItem.IsChecked = string.Equals(currentEffect, "Honeycomb", StringComparison.OrdinalIgnoreCase);
        if (EffectBlurItem != null) EffectBlurItem.IsChecked = string.Equals(currentEffect, "Blur", StringComparison.OrdinalIgnoreCase);

        if (WallpaperEffectsMenu != null) WallpaperEffectsMenu.IsEnabled = isWallpaper;
    }
}
