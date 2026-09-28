using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Core.Services;

namespace MetroHub;

public partial class MainWindow
{
    private int _wallpaperLoadGeneration = 0;
    private string? _currentLoadedWallpaperPath = null;
    private CancellationTokenSource? _toastCts;
    private CancellationTokenSource? _wallpaperCts;

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
                Title = "Select Custom Wallpaper or Video",
                Filter = DailyWallpaperService.GetWallpaperFileDialogFilter()
            };

            if (dialog.ShowDialog(this) == true)
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

    private void OnWallpaperDimLightClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.35);
    private void OnWallpaperDimBalancedClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.50);
    private void OnWallpaperDimHeavyClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.65);

    private void SetWallpaperDim(double opacity)
    {
        Settings.WallpaperDimOpacity = opacity;
        StorageService.SaveSettings(Settings);
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

    public void ApplyConfiguredBackdrop()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        Background = System.Windows.Media.Brushes.Transparent;
        var hs = HwndSource.FromHwnd(hwnd);
        if (hs?.CompositionTarget != null)
        {
            hs.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        }

        bool isWallpaper = IsWallpaperBackdrop(Settings.BackdropType);

        if (isWallpaper)
        {
            var ct = BeginNewWallpaperGeneration();
            NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_NONE);
            if (RootGrid != null)
            {
                RootGrid.Background = System.Windows.Media.Brushes.Transparent;
            }
            _ = UpdateWallpaperDisplayAsync(ct);
        }
        else
        {
            CancelPendingWallpaperLoad();
            TeardownWallpaperVideo();

            if (CustomWallpaperHost != null)
            {
                CustomWallpaperHost.Visibility = Visibility.Collapsed;
            }

            if (WallpaperImage != null && WallpaperImage.Source != null)
            {
                WallpaperImage.Source = null;
                _currentLoadedWallpaperPath = null;
            }

            if (string.Equals(Settings.BackdropType, "Acrylic", StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TRANSIENTWINDOW);
                if (RootGrid != null)
                {
                    var acrylicBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x99, 0x0D, 0x0D, 0x11));
                    acrylicBrush.Freeze();
                    RootGrid.Background = acrylicBrush;
                }
            }
            else if (string.Equals(Settings.BackdropType, "MicaAlt", StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TABBEDWINDOW);
                if (RootGrid != null)
                {
                    RootGrid.Background = System.Windows.Media.Brushes.Transparent;
                }
            }
            else
            {
                NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_MAINWINDOW);
                if (RootGrid != null)
                {
                    RootGrid.Background = System.Windows.Media.Brushes.Transparent;
                }
            }
        }

        UpdateBackdropMenuChecks();
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

    private CancellationToken BeginNewWallpaperGeneration()
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

    private void CancelPendingWallpaperLoad()
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
        string failedPath = _currentLoadedWallpaperPath ?? string.Empty;
        _currentLoadedWallpaperPath = null;
        TeardownWallpaperVideo();
        ShowToast(GetMissingCodecMessage(failedPath), isError: true);
    }

    public void ShowToast(string message, bool isError = false)
    {
        if (NotificationToast == null || NotificationToastText == null || NotificationToastIcon == null) return;

        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        NotificationToastText.Text = message;
        NotificationToastIcon.Symbol = isError ? Wpf.Ui.Controls.SymbolRegular.Warning24 : Wpf.Ui.Controls.SymbolRegular.Info24;
        var iconBrush = isError
            ? new SolidColorBrush(Color.FromRgb(255, 120, 120))
            : new SolidColorBrush(Color.FromRgb(96, 205, 255));
        iconBrush.Freeze();
        NotificationToastIcon.Foreground = iconBrush;

        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200));
        NotificationToast.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        Task.Delay(4000, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.InvokeAsync(() =>
            {
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(250));
                NotificationToast.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            });
        }, token);
    }

    private async Task UpdateWallpaperDisplayAsync(CancellationToken ct)
    {
        if (CustomWallpaperHost == null || WallpaperImage == null || WallpaperScrim == null) return;

        int currentGen = _wallpaperLoadGeneration;
        string backdropType = Settings.BackdropType;

        // Apply scrim dim opacity immediately with frozen Freezable brush
        double dim = Math.Clamp(Settings.WallpaperDimOpacity, 0.1, 0.9);
        byte alpha = (byte)(255 * dim);
        var scrimBrush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        scrimBrush.Freeze();
        WallpaperScrim.Background = scrimBrush;

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
                imagePath = Settings.CustomWallpaperPath;
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
                    if (WallpaperVideo == null) return;

                    // If already playing this exact video, ensure visibility and update parallax
                    if (string.Equals(_currentLoadedWallpaperPath, imagePath, StringComparison.OrdinalIgnoreCase) &&
                        WallpaperVideo.Source != null && WallpaperVideo.Visibility == Visibility.Visible)
                    {
                        CustomWallpaperHost.Visibility = Visibility.Visible;
                        UpdateWallpaperParallax();
                        return;
                    }

                    // Release any static image memory
                    if (WallpaperImage != null)
                    {
                        WallpaperImage.Source = null;
                        WallpaperImage.Visibility = Visibility.Collapsed;
                    }

                    try
                    {
                        _currentLoadedWallpaperPath = imagePath;
                        var uri = new Uri(imagePath, UriKind.Absolute);
                        if (!Equals(WallpaperVideo.Source, uri))
                        {
                            WallpaperVideo.Source = uri;
                        }
                        WallpaperVideo.Visibility = Visibility.Visible;
                        CustomWallpaperHost.Visibility = Visibility.Visible;
                        UpdateWallpaperParallax();
                        WallpaperVideo.Play();
                    }
                    catch (Exception)
                    {
                        _currentLoadedWallpaperPath = null;
                        TeardownWallpaperVideo();
                        ShowToast(GetMissingCodecMessage(imagePath), isError: true);
                    }
                    return;
                }

                // Static image wallpaper path: release any video decoder resources
                TeardownWallpaperVideo();

                // If the exact same wallpaper is already loaded and decoded, reuse it without disk I/O or re-decoding
                if (string.Equals(_currentLoadedWallpaperPath, imagePath, StringComparison.OrdinalIgnoreCase) && WallpaperImage.Source != null)
                {
                    WallpaperImage.Visibility = Visibility.Visible;
                    CustomWallpaperHost.Visibility = Visibility.Visible;
                    UpdateWallpaperParallax();
                    return;
                }

                // Decode at exact 1:1 screen pixel width (up to 3840 max on 4K, downscale only)
                double screenW = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;

                var (bmp, error) = await DailyWallpaperService.TryLoadWallpaperAsync(imagePath, screenW, ct).ConfigureAwait(true);
                lastError = error;

                if (ct.IsCancellationRequested || currentGen != _wallpaperLoadGeneration) return;

                if (bmp != null)
                {
                    _currentLoadedWallpaperPath = imagePath;
                    WallpaperImage.Source = bmp;
                    WallpaperImage.Visibility = Visibility.Visible;
                    CustomWallpaperHost.Visibility = Visibility.Visible;
                    UpdateWallpaperParallax();

                    // Subtle, silky smooth fade-in
                    var anim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(300));
                    WallpaperImage.BeginAnimation(UIElement.OpacityProperty, anim);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when user switches backdrop before download/decode completes
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateWallpaperDisplayAsync error: {ex.Message}");
            lastError ??= ex.Message;
        }

        if (ct.IsCancellationRequested || currentGen != _wallpaperLoadGeneration) return;

        // Fallback to dark background if no image or video could be loaded
        _currentLoadedWallpaperPath = null;
        if (WallpaperImage != null)
        {
            WallpaperImage.Source = null;
            WallpaperImage.Visibility = Visibility.Collapsed;
        }
        TeardownWallpaperVideo();
        if (CustomWallpaperHost != null)
        {
            CustomWallpaperHost.Visibility = Visibility.Collapsed;
        }
        if (RootGrid != null)
        {
            var darkBrush = new SolidColorBrush(Color.FromArgb(0xEE, 0x10, 0x10, 0x14));
            darkBrush.Freeze();
            RootGrid.Background = darkBrush;
        }

        if (!string.IsNullOrEmpty(lastError) && string.Equals(backdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase))
        {
            ShowToast(lastError, isError: true);
        }
        UpdateWallpaperParallax();
    }

    private void OnContentScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateWallpaperParallax();
    }

    private void UpdateWallpaperParallax()
    {
        if (WallpaperTranslateTransform == null && WallpaperVideoTranslateTransform == null) return;

        bool isWallpaper = IsWallpaperBackdrop(Settings.BackdropType);

        if (!isWallpaper || !Settings.WallpaperParallax || ContentScrollViewer == null)
        {
            if (WallpaperTranslateTransform != null) WallpaperTranslateTransform.Y = 0;
            if (WallpaperVideoTranslateTransform != null) WallpaperVideoTranslateTransform.Y = 0;
            return;
        }

        const double maxShift = 90.0;

        if (ContentScrollViewer.ScrollableHeight > 0)
        {
            double ratio = Math.Clamp(ContentScrollViewer.VerticalOffset / ContentScrollViewer.ScrollableHeight, 0.0, 1.0);
            double shift = -ratio * maxShift;
            if (WallpaperTranslateTransform != null) WallpaperTranslateTransform.Y = shift;
            if (WallpaperVideoTranslateTransform != null) WallpaperVideoTranslateTransform.Y = shift;
        }
        else
        {
            if (WallpaperTranslateTransform != null) WallpaperTranslateTransform.Y = 0;
            if (WallpaperVideoTranslateTransform != null) WallpaperVideoTranslateTransform.Y = 0;
        }
    }

    private static string GetMissingCodecMessage(string path)
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

    private void TeardownWallpaperVideo()
    {
        if (WallpaperVideo == null) return;
        try
        {
            WallpaperVideo.Stop();
            WallpaperVideo.Close();
            WallpaperVideo.Source = null;
        }
        catch { }
        WallpaperVideo.Visibility = Visibility.Collapsed;
    }

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
        if (DimLightItem != null) DimLightItem.IsChecked = Math.Abs(dim - 0.35) < 0.05;
        if (DimBalancedItem != null) DimBalancedItem.IsChecked = Math.Abs(dim - 0.50) < 0.05;
        if (DimHeavyItem != null) DimHeavyItem.IsChecked = Math.Abs(dim - 0.65) < 0.05;

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
    }
}
