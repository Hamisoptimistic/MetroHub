using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Themes;

namespace MetroHub.Presentation.Controllers;

/// <summary>
/// Manages window backdrops (Mica, Acrylic, Windows Desktop Wallpaper, Custom Wallpapers, 
/// Bing Daily, Windows Spotlight Daily, video wallpapers, dimming, and parallax scrolling).
/// </summary>
public sealed class BackdropManager
{
    private static readonly SolidColorBrush AcrylicTintBrush = ThemeTokens.CreateFrozenBrush(Color.FromArgb(0x99, 0x0D, 0x0D, 0x11));
    private static readonly SolidColorBrush FallbackDarkBrush = ThemeTokens.CreateFrozenBrush(Color.FromArgb(0xEE, 0x10, 0x10, 0x14));

    private readonly Window _window;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly Panel? _rootGrid;
    private readonly FrameworkElement? _customWallpaperHost;
    private readonly Image? _wallpaperImage;
    private readonly MediaElement? _wallpaperVideo;
    private readonly Border? _wallpaperScrim;
    private readonly TranslateTransform? _wallpaperTranslateTransform;
    private readonly TranslateTransform? _wallpaperVideoTranslateTransform;
    private readonly ScrollViewer? _contentScrollViewer;
    private readonly Action<string, bool> _toastAction;
    private readonly Action? _updateMenuChecksAction;

    private int _wallpaperLoadGeneration = 0;
    private string? _currentLoadedWallpaperPath = null;
    private CancellationTokenSource? _wallpaperCts;

    public string? CurrentLoadedWallpaperPath => _currentLoadedWallpaperPath;

    public BackdropManager(
        Window window,
        Func<AppSettings> settingsProvider,
        Panel? rootGrid,
        FrameworkElement? customWallpaperHost,
        Image? wallpaperImage,
        MediaElement? wallpaperVideo,
        Border? wallpaperScrim,
        TranslateTransform? wallpaperTranslateTransform,
        TranslateTransform? wallpaperVideoTranslateTransform,
        ScrollViewer? contentScrollViewer,
        Action<string, bool> toastAction,
        Action? updateMenuChecksAction = null)
    {
        _window = window;
        _settingsProvider = settingsProvider;
        _rootGrid = rootGrid;
        _customWallpaperHost = customWallpaperHost;
        _wallpaperImage = wallpaperImage;
        _wallpaperVideo = wallpaperVideo;
        _wallpaperScrim = wallpaperScrim;
        _wallpaperTranslateTransform = wallpaperTranslateTransform;
        _wallpaperVideoTranslateTransform = wallpaperVideoTranslateTransform;
        _contentScrollViewer = contentScrollViewer;
        _toastAction = toastAction;
        _updateMenuChecksAction = updateMenuChecksAction;
    }

    public void ApplyConfiguredBackdrop()
    {
        IntPtr hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;

        _window.Background = System.Windows.Media.Brushes.Transparent;
        var hs = HwndSource.FromHwnd(hwnd);
        if (hs?.CompositionTarget != null)
        {
            hs.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        }

        var settings = _settingsProvider();
        bool isWallpaper = IsWallpaperBackdrop(settings.BackdropType);

        if (isWallpaper)
        {
            var ct = BeginNewWallpaperGeneration();
            NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_NONE);
            if (_rootGrid != null)
            {
                _rootGrid.Background = System.Windows.Media.Brushes.Transparent;
            }
            _ = UpdateWallpaperDisplayAsync(ct);
        }
        else
        {
            CancelPendingWallpaperLoad();
            TeardownWallpaperVideo();

            if (_customWallpaperHost != null)
            {
                _customWallpaperHost.Visibility = Visibility.Collapsed;
            }

            if (_wallpaperImage != null && _wallpaperImage.Source != null)
            {
                _wallpaperImage.Source = null;
                _currentLoadedWallpaperPath = null;
            }

            if (string.Equals(settings.BackdropType, "Acrylic", StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TRANSIENTWINDOW);
                if (_rootGrid != null)
                {
                    _rootGrid.Background = AcrylicTintBrush;
                }
            }
            else if (string.Equals(settings.BackdropType, "MicaAlt", StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TABBEDWINDOW);
                if (_rootGrid != null)
                {
                    _rootGrid.Background = System.Windows.Media.Brushes.Transparent;
                }
            }
            else
            {
                NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_MAINWINDOW);
                if (_rootGrid != null)
                {
                    _rootGrid.Background = System.Windows.Media.Brushes.Transparent;
                }
            }
        }

        _updateMenuChecksAction?.Invoke();
    }

    public void UpdateWallpaperParallax()
    {
        if (_wallpaperTranslateTransform == null && _wallpaperVideoTranslateTransform == null) return;

        var settings = _settingsProvider();
        bool isWallpaper = IsWallpaperBackdrop(settings.BackdropType);

        if (!isWallpaper || !settings.WallpaperParallax || _contentScrollViewer == null)
        {
            if (_wallpaperTranslateTransform != null) _wallpaperTranslateTransform.Y = 0;
            if (_wallpaperVideoTranslateTransform != null) _wallpaperVideoTranslateTransform.Y = 0;
            return;
        }

        const double maxShift = 90.0;

        if (_contentScrollViewer.ScrollableHeight > 0)
        {
            double ratio = Math.Clamp(_contentScrollViewer.VerticalOffset / _contentScrollViewer.ScrollableHeight, 0.0, 1.0);
            double shift = -ratio * maxShift;
            if (_wallpaperTranslateTransform != null) _wallpaperTranslateTransform.Y = shift;
            if (_wallpaperVideoTranslateTransform != null) _wallpaperVideoTranslateTransform.Y = shift;
        }
        else
        {
            if (_wallpaperTranslateTransform != null) _wallpaperTranslateTransform.Y = 0;
            if (_wallpaperVideoTranslateTransform != null) _wallpaperVideoTranslateTransform.Y = 0;
        }
    }

    public void SetWallpaperDim(double opacity)
    {
        var settings = _settingsProvider();
        settings.WallpaperDimOpacity = opacity;
        StorageService.SaveSettings(settings);
        ApplyConfiguredBackdrop();
    }

    public void TeardownWallpaperVideo()
    {
        if (_wallpaperVideo == null) return;
        try
        {
            _wallpaperVideo.Stop();
            _wallpaperVideo.Close();
            _wallpaperVideo.Source = null;
        }
        catch { }
        _wallpaperVideo.Visibility = Visibility.Collapsed;
    }

    public CancellationToken BeginNewWallpaperGeneration()
    {
        _wallpaperLoadGeneration++;
        if (_wallpaperCts != null)
        {
            try
            {
                _wallpaperCts.Cancel();
                _wallpaperCts.Dispose();
            }
            catch { }
            _wallpaperCts = null;
        }

        _wallpaperCts = new CancellationTokenSource();
        return _wallpaperCts.Token;
    }

    public void CancelPendingWallpaperLoad()
    {
        _wallpaperLoadGeneration++;
        if (_wallpaperCts != null)
        {
            try
            {
                _wallpaperCts.Cancel();
                _wallpaperCts.Dispose();
            }
            catch { }
            _wallpaperCts = null;
        }
    }

    public async Task UpdateWallpaperDisplayAsync(CancellationToken ct)
    {
        if (_customWallpaperHost == null || _wallpaperImage == null || _wallpaperScrim == null) return;

        int currentGen = _wallpaperLoadGeneration;
        var settings = _settingsProvider();
        string backdropType = settings.BackdropType;

        // Apply scrim dim opacity immediately with frozen Freezable brush
        double dim = Math.Clamp(settings.WallpaperDimOpacity, 0.1, 0.9);
        byte alpha = (byte)(255 * dim);
        var scrimBrush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        scrimBrush.Freeze();
        _wallpaperScrim.Background = scrimBrush;

        string? lastError = null;

        try
        {
            if (ct.IsCancellationRequested || currentGen != _wallpaperLoadGeneration) return;

            string? imagePath = null;

            if (string.Equals(backdropType, "DesktopWallpaper", StringComparison.OrdinalIgnoreCase))
            {
                imagePath = GetActiveDesktopWallpaperPath();
            }
            else if (string.Equals(backdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase))
            {
                imagePath = settings.CustomWallpaperPath;
            }
            else if (string.Equals(backdropType, "BingDaily", StringComparison.OrdinalIgnoreCase))
            {
                imagePath = await DailyWallpaperService.Instance.GetBingDailyWallpaperAsync(ct).ConfigureAwait(true);
            }
            else if (string.Equals(backdropType, "SpotlightDaily", StringComparison.OrdinalIgnoreCase))
            {
                imagePath = await DailyWallpaperService.Instance.GetSpotlightDailyWallpaperAsync(ct).ConfigureAwait(true);
            }

            // If user switched backdrop while awaiting, discard this stale result
            if (ct.IsCancellationRequested || currentGen != _wallpaperLoadGeneration) return;

            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                if (DailyWallpaperService.IsVideoWallpaper(imagePath))
                {
                    if (_wallpaperVideo == null) return;

                    // If already playing this exact video, ensure visibility and update parallax
                    if (string.Equals(_currentLoadedWallpaperPath, imagePath, StringComparison.OrdinalIgnoreCase) &&
                        _wallpaperVideo.Source != null && _wallpaperVideo.Visibility == Visibility.Visible)
                    {
                        _customWallpaperHost.Visibility = Visibility.Visible;
                        UpdateWallpaperParallax();
                        return;
                    }

                    // Release any static image memory
                    if (_wallpaperImage != null)
                    {
                        _wallpaperImage.Source = null;
                        _wallpaperImage.Visibility = Visibility.Collapsed;
                    }

                    try
                    {
                        _currentLoadedWallpaperPath = imagePath;
                        var uri = new Uri(imagePath, UriKind.Absolute);
                        if (!Equals(_wallpaperVideo.Source, uri))
                        {
                            _wallpaperVideo.Source = uri;
                        }
                        _wallpaperVideo.Visibility = Visibility.Visible;
                        _customWallpaperHost.Visibility = Visibility.Visible;
                        UpdateWallpaperParallax();
                        _wallpaperVideo.Play();
                    }
                    catch (Exception)
                    {
                        _currentLoadedWallpaperPath = null;
                        TeardownWallpaperVideo();
                        _toastAction(GetMissingCodecMessage(imagePath), true);
                    }
                    return;
                }

                // Static image wallpaper path: release any video decoder resources
                TeardownWallpaperVideo();

                // If the exact same wallpaper is already loaded and decoded, reuse it without disk I/O or re-decoding
                if (string.Equals(_currentLoadedWallpaperPath, imagePath, StringComparison.OrdinalIgnoreCase) && _wallpaperImage.Source != null)
                {
                    _wallpaperImage.Visibility = Visibility.Visible;
                    _customWallpaperHost.Visibility = Visibility.Visible;
                    UpdateWallpaperParallax();
                    return;
                }

                // Decode at exact 1:1 screen pixel width (up to 3840 max on 4K, downscale only)
                double screenW = _window.ActualWidth > 0 ? _window.ActualWidth : SystemParameters.PrimaryScreenWidth;

                var (bmp, error) = await DailyWallpaperService.TryLoadWallpaperAsync(imagePath, screenW, ct).ConfigureAwait(true);
                lastError = error;

                if (ct.IsCancellationRequested || currentGen != _wallpaperLoadGeneration) return;

                if (bmp != null)
                {
                    _currentLoadedWallpaperPath = imagePath;
                    _wallpaperImage.Source = bmp;
                    _wallpaperImage.Visibility = Visibility.Visible;
                    _customWallpaperHost.Visibility = Visibility.Visible;
                    UpdateWallpaperParallax();

                    // Subtle, silky smooth fade-in
                    var anim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(300));
                    _wallpaperImage.BeginAnimation(UIElement.OpacityProperty, anim);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BackdropManager] UpdateWallpaperDisplayAsync error: {ex.Message}");
            lastError ??= ex.Message;
        }

        if (ct.IsCancellationRequested || currentGen != _wallpaperLoadGeneration) return;

        // Fallback to dark background if no image or video could be loaded
        _currentLoadedWallpaperPath = null;
        if (_wallpaperImage != null)
        {
            _wallpaperImage.Source = null;
            _wallpaperImage.Visibility = Visibility.Collapsed;
        }
        TeardownWallpaperVideo();
        if (_customWallpaperHost != null)
        {
            _customWallpaperHost.Visibility = Visibility.Collapsed;
        }
        if (_rootGrid != null)
        {
            _rootGrid.Background = FallbackDarkBrush;
        }

        if (!string.IsNullOrEmpty(lastError) && string.Equals(backdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase))
        {
            _toastAction(lastError, true);
        }
        UpdateWallpaperParallax();
    }

    public static bool IsWallpaperBackdrop(string? backdropType)
    {
        return string.Equals(backdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(backdropType, "DesktopWallpaper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(backdropType, "BingDaily", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(backdropType, "SpotlightDaily", StringComparison.OrdinalIgnoreCase);
    }

    public static BitmapImage? LoadOptimizedBitmap(string path, int decodeWidth = 1920)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (decodeWidth > 0)
            {
                bitmap.DecodePixelWidth = decodeWidth;
            }
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public static string? GetActiveDesktopWallpaperPath()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string transcoded = Path.Combine(appData, @"Microsoft\Windows\Themes\TranscodedWallpaper");
            if (File.Exists(transcoded))
            {
                return transcoded;
            }

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string? regPath = key?.GetValue("WallPaper") as string;
            if (!string.IsNullOrEmpty(regPath) && File.Exists(regPath))
            {
                return regPath;
            }
        }
        catch { }

        return null;
    }

    public static string GetMissingCodecMessage(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        return ext.ToLowerInvariant() switch
        {
            ".webm" => "WebM codec missing. Install 'Web Media Extensions' from Microsoft Store or use .mp4.",
            ".mkv" => "MKV / HEVC codec missing. Install 'HEVC Video Extensions' from Microsoft Store.",
            ".mov" or ".qt" => "QuickTime codec missing. Convert to standard .mp4 (H.264).",
            ".avi" or ".divx" or ".xvid" => "AVI / DivX codec missing. Install K-Lite Codec Pack or use .mp4.",
            ".flv" or ".f4v" => "Flash Video codec missing. Convert to standard .mp4.",
            ".ts" or ".mts" or ".m2ts" or ".vob" or ".mpg" or ".mpeg" => "MPEG-2 codec missing. Install 'MPEG-2 Video Extension' from Microsoft Store.",
            ".ogv" or ".ogg" => "Ogg video codec missing. Install 'Web Media Extensions' from Microsoft Store.",
            ".wmv" or ".asf" => "Windows Media codec error. Enable Media Feature Pack in Windows Features.",
            _ => "Unsupported video codec. Try standard .mp4 (H.264) or install required codec."
        };
    }
}
