using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Models;
using MetroHub.Widgets.Messaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MetroHub.Widgets.Catalog.Media;

public partial class MediaWidgetViewModel : WidgetViewModelBase, IRecipient<HubVisibilityChangedMessage>
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private bool _isHubVisible = true;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Mega,    // 8x4
        WidgetSize.Banner3  // 8x3
    };

    [ObservableProperty]
    private string _title = "No media playing";

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    private string _album = string.Empty;

    [ObservableProperty]
    private bool _hasAlbum;

    [ObservableProperty]
    private string _sourceName = "Media Player";

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _hasThumbnail;

    [ObservableProperty]
    private Brush? _glowBrush;

    [ObservableProperty]
    private Brush? _sensualRadialBrush;

    [ObservableProperty]
    private SolidColorBrush _glowSolidBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x82, 0xD4));

    [ObservableProperty]
    private Color _glowColor = Color.FromRgb(0x3A, 0x82, 0xD4);

    public double AlbumArtSize => Model.SpanY >= 4 ? 132.0 : 104.0;

    public double GlowBlurRadius => Model.SpanY >= 4 ? 30.0 : 20.0;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _hasMedia;

    [ObservableProperty]
    private bool _canPlayPause = true;

    [ObservableProperty]
    private bool _canSkipNext = true;

    [ObservableProperty]
    private bool _canSkipPrevious = true;

    public MediaWidgetViewModel(TileModel model) : base(model)
    {
        if (model.SpanX != 8 || (model.SpanY != 4 && model.SpanY != 3))
        {
            model.SpanX = 8;
            model.SpanY = 4;
        }

        model.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(TileModel.SpanX) or nameof(TileModel.SpanY))
            {
                OnPropertyChanged(nameof(AlbumArtSize));
                OnPropertyChanged(nameof(GlowBlurRadius));
            }
        };

        InitializeAsync();
    }

    private void InitializeAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_manager != null)
                {
                    _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
                    await RefreshSessionAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaWidget] Failed to initialize session manager: {ex.Message}");
            }
        });
    }

    private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        _ = RefreshSessionAsync();
    }

    public async Task RefreshSessionAsync()
    {
        if (_manager == null) return;

        GlobalSystemMediaTransportControlsSession? newSession = null;
        try
        {
            newSession = _manager.GetCurrentSession();
        }
        catch { }

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
        }

        _currentSession = newSession;

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            await UpdateMediaDetailsAsync();
        }
        else
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HasMedia = false;
                Title = "No media playing";
                Artist = "Open Spotify, YouTube, or VLC";
                Album = string.Empty;
                HasAlbum = false;
                SourceName = "Media Player";
                Thumbnail = null;
                HasThumbnail = false;
                GlowBrush = null;
                SensualRadialBrush = null;
                GlowColor = Color.FromRgb(0x3A, 0x82, 0xD4);
                var fallbackBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x82, 0xD4));
                fallbackBrush.Freeze();
                GlowSolidBrush = fallbackBrush;
                IsPlaying = false;
            });
        }
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = UpdateMediaDetailsAsync();
    }

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        UpdatePlaybackInfo();
    }

    private void UpdatePlaybackInfo()
    {
        if (_currentSession == null) return;

        try
        {
            var playback = _currentSession.GetPlaybackInfo();
            if (playback != null)
            {
                bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var controls = playback.Controls;

                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsPlaying = playing;
                    CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? true;
                    CanSkipNext = controls?.IsNextEnabled ?? true;
                    CanSkipPrevious = controls?.IsPreviousEnabled ?? true;
                });
            }
        }
        catch { }
    }

    private async Task UpdateMediaDetailsAsync()
    {
        if (!_isHubVisible || _currentSession == null) return;

        try
        {
            var props = await _currentSession.TryGetMediaPropertiesAsync();
            var playback = _currentSession.GetPlaybackInfo();
            string rawSource = _currentSession.SourceAppUserModelId ?? string.Empty;

            if (props == null || (string.IsNullOrWhiteSpace(props.Title) && string.IsNullOrWhiteSpace(props.Artist)))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    HasMedia = false;
                    Title = "No media playing";
                    Artist = "Open Spotify, YouTube, or VLC";
                    Album = string.Empty;
                    HasAlbum = false;
                    SourceName = ResolveSourceName(rawSource, null, null);
                    Thumbnail = null;
                    HasThumbnail = false;
                    var fallbackBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x82, 0xD4));
                    fallbackBrush.Freeze();
                    GlowSolidBrush = fallbackBrush;
                    SensualRadialBrush = null;
                    GlowBrush = null;
                    GlowColor = Color.FromRgb(0x3A, 0x82, 0xD4);
                });
                return;
            }

            string cleanTitle = props.Title?.Trim() ?? "Unknown Track";
            string cleanArtist = props.Artist?.Trim() ?? string.Empty;
            string cleanAlbum = props.AlbumTitle?.Trim() ?? props.AlbumArtist?.Trim() ?? string.Empty;

            // Strip redundant browser suffixes like " - YouTube"
            if (cleanTitle.EndsWith(" - YouTube", StringComparison.OrdinalIgnoreCase))
            {
                cleanTitle = cleanTitle.Substring(0, cleanTitle.Length - " - YouTube".Length).Trim();
            }

            // Clean YouTube Music "- Topic" suffix from artist name (e.g. "Deftones - Topic" -> "Deftones")
            string rawArtist = cleanArtist;
            if (cleanArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
            {
                cleanArtist = cleanArtist.Substring(0, cleanArtist.Length - " - Topic".Length).Trim();
            }
            else if (cleanArtist.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase))
            {
                cleanArtist = cleanArtist.Substring(0, cleanArtist.Length - "- Topic".Length).Trim();
            }

            // If artist is empty but title has " - ", split artist and track name (common on YouTube/browser streams)
            if (string.IsNullOrWhiteSpace(cleanArtist) && cleanTitle.Contains(" - "))
            {
                var parts = cleanTitle.Split(new[] { " - " }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    cleanArtist = parts[0].Trim();
                    cleanTitle = parts[1].Trim();
                }
            }

            string cleanSource = ResolveSourceName(rawSource, cleanTitle, rawArtist);
            bool hasAlbum = !string.IsNullOrWhiteSpace(cleanAlbum);

            bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var controls = playback?.Controls;

            ImageSource? bmp = null;
            Color glowColor = Color.FromRgb(0x3A, 0x82, 0xD4);
            var glowSolidBrush = new SolidColorBrush(glowColor);
            glowSolidBrush.Freeze();
            RadialGradientBrush? sensualRadialBrush = null;

            if (props.Thumbnail != null)
            {
                bmp = await LoadThumbnailAsync(props.Thumbnail);
                if (bmp is BitmapSource bs)
                {
                    (glowColor, glowSolidBrush, sensualRadialBrush) = CreateGlow(bs);
                }
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Title = cleanTitle;
                Artist = cleanArtist;
                Album = cleanAlbum;
                HasAlbum = hasAlbum;
                SourceName = cleanSource;
                Thumbnail = bmp;
                HasThumbnail = bmp != null;
                GlowSolidBrush = glowSolidBrush;
                SensualRadialBrush = sensualRadialBrush;
                GlowBrush = sensualRadialBrush;
                GlowColor = glowColor;
                IsPlaying = playing;
                HasMedia = true;
                CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? true;
                CanSkipNext = controls?.IsNextEnabled ?? true;
                CanSkipPrevious = controls?.IsPreviousEnabled ?? true;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaWidget] UpdateMediaDetails error: {ex.Message}");
        }
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference streamRef)
    {
        try
        {
            using var stream = await streamRef.OpenReadAsync();
            using var netStream = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await netStream.CopyToAsync(memory);
            memory.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();

            // If thumbnail is 16:9 YouTube video frame with pillarboxes,
            // center-crop to the square album cover to eliminate side pillarbox bars!
            if (bitmap.PixelWidth > bitmap.PixelHeight * 1.25)
            {
                int size = bitmap.PixelHeight;
                int xOffset = (bitmap.PixelWidth - size) / 2;
                var cropped = new CroppedBitmap(bitmap, new Int32Rect(xOffset, 0, size, size));
                cropped.Freeze();
                return cropped;
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static (Color, SolidColorBrush, RadialGradientBrush) CreateGlow(BitmapSource bitmap)
    {
        try
        {
            var thumb = new TransformedBitmap(bitmap, new ScaleTransform(16.0 / bitmap.PixelWidth, 16.0 / bitmap.PixelHeight));
            var converted = new FormatConvertedBitmap(thumb, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            converted.CopyPixels(pixels, stride, 0);

            double totalColorWeight = 0;
            double accR = 0, accG = 0, accB = 0;
            int validPixelCount = 0;
            double totalLum = 0;

            for (int i = 0; i <= pixels.Length - 4; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];
                if (a < 128) continue;

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                int delta = max - min;

                var (h, s, v) = RgbToHsv(r, g, b);
                validPixelCount++;
                totalLum += v;

                // Strict check for genuine chromatic color:
                // delta >= 24 ensures grayscale/white compression artifacts (where r is 1-2 units > b)
                // are NEVER mistaken for red hue 0!
                if (delta >= 24 && s >= 0.20 && v >= 0.18 && v <= 0.95)
                {
                    double weight = s * s * (1.0 - Math.Abs(v - 0.65));
                    accR += r * weight;
                    accG += g * weight;
                    accB += b * weight;
                    totalColorWeight += weight;
                }
            }

            Color accent;
            bool isMonochrome = totalColorWeight < 0.05;

            if (!isMonochrome)
            {
                byte avgR = (byte)Math.Clamp(accR / totalColorWeight, 0, 255);
                byte avgG = (byte)Math.Clamp(accG / totalColorWeight, 0, 255);
                byte avgB = (byte)Math.Clamp(accB / totalColorWeight, 0, 255);

                var (h, s, v) = RgbToHsv(avgR, avgG, avgB);
                s = Math.Clamp(s * 1.3, 0.45, 0.95);
                v = Math.Clamp(v * 1.15, 0.55, 0.88);
                accent = ColorFromHsv(h, s, v);
            }
            else
            {
                // Monochromatic / White / Black album art:
                // Produce a sensual, luminous moonlight pearl-white aura — NOT red!
                double avgLum = validPixelCount > 0 ? totalLum / validPixelCount : 0.8;
                if (avgLum > 0.4)
                {
                    // White or light-gray album art -> Pure pearl-white
                    accent = Color.FromRgb(242, 246, 255);
                }
                else
                {
                    // Dark / black album art -> Soft silver moonlight
                    accent = Color.FromRgb(215, 228, 245);
                }
            }

            var solidBrush = new SolidColorBrush(accent);
            solidBrush.Freeze();

            // Sensual, fluent multi-stop radial gradient originating from behind the album art
            // diffusing across the entire card with a smooth non-linear cubic decay curve
            byte a0 = isMonochrome ? (byte)100 : (byte)140;
            byte a1 = isMonochrome ? (byte)75  : (byte)105;
            byte a2 = isMonochrome ? (byte)48  : (byte)66;
            byte a3 = isMonochrome ? (byte)24  : (byte)34;
            byte a4 = isMonochrome ? (byte)8   : (byte)12;

            var sensualBrush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                Center = new Point(0.80, 0.42),
                GradientOrigin = new Point(0.80, 0.42),
                RadiusX = 0.95,
                RadiusY = 1.15
            };
            sensualBrush.GradientStops.Add(new GradientStop(Color.FromArgb(a0, accent.R, accent.G, accent.B), 0.00));
            sensualBrush.GradientStops.Add(new GradientStop(Color.FromArgb(a1, accent.R, accent.G, accent.B), 0.18));
            sensualBrush.GradientStops.Add(new GradientStop(Color.FromArgb(a2, accent.R, accent.G, accent.B), 0.38));
            sensualBrush.GradientStops.Add(new GradientStop(Color.FromArgb(a3, accent.R, accent.G, accent.B), 0.60));
            sensualBrush.GradientStops.Add(new GradientStop(Color.FromArgb(a4, accent.R, accent.G, accent.B), 0.82));
            sensualBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0,  accent.R, accent.G, accent.B), 1.00));
            sensualBrush.Freeze();

            return (accent, solidBrush, sensualBrush);
        }
        catch
        {
            var fallbackColor = Color.FromRgb(0x3A, 0x82, 0xD4);
            var fallbackSolid = new SolidColorBrush(fallbackColor);
            fallbackSolid.Freeze();

            var fallbackSensual = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                Center = new Point(0.80, 0.42),
                GradientOrigin = new Point(0.80, 0.42),
                RadiusX = 0.95,
                RadiusY = 1.15
            };
            fallbackSensual.GradientStops.Add(new GradientStop(Color.FromArgb(140, 0x3A, 0x82, 0xD4), 0.00));
            fallbackSensual.GradientStops.Add(new GradientStop(Color.FromArgb(105, 0x3A, 0x82, 0xD4), 0.18));
            fallbackSensual.GradientStops.Add(new GradientStop(Color.FromArgb(66,  0x3A, 0x82, 0xD4), 0.38));
            fallbackSensual.GradientStops.Add(new GradientStop(Color.FromArgb(34,  0x3A, 0x82, 0xD4), 0.60));
            fallbackSensual.GradientStops.Add(new GradientStop(Color.FromArgb(12,  0x3A, 0x82, 0xD4), 0.82));
            fallbackSensual.GradientStops.Add(new GradientStop(Color.FromArgb(0,   0x3A, 0x82, 0xD4), 1.00));
            fallbackSensual.Freeze();

            return (fallbackColor, fallbackSolid, fallbackSensual);
        }
    }

    private static (double h, double s, double v) RgbToHsv(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == rd) h = ((gd - bd) / delta) % 6.0;
            else if (max == gd) h = ((bd - rd) / delta) + 2.0;
            else h = ((rd - gd) / delta) + 4.0;
            h *= 60.0;
            if (h < 0) h += 360.0;
        }

        double s = max == 0 ? 0 : delta / max;
        double v = max;
        return (h, s, v);
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
        double f = hue / 60 - Math.Floor(hue / 60);

        value = value * 255;
        byte v = (byte)Math.Clamp(value, 0, 255);
        byte p = (byte)Math.Clamp(value * (1 - saturation), 0, 255);
        byte q = (byte)Math.Clamp(value * (1 - f * saturation), 0, 255);
        byte t = (byte)Math.Clamp(value * (1 - (1 - f) * saturation), 0, 255);

        return hi switch
        {
            0 => Color.FromRgb(v, t, p),
            1 => Color.FromRgb(q, v, p),
            2 => Color.FromRgb(p, v, t),
            3 => Color.FromRgb(p, q, v),
            4 => Color.FromRgb(t, p, v),
            _ => Color.FromRgb(v, p, q),
        };
    }

    public static string ResolveSourceName(string? appId, string? title, string? artist)
    {
        // 1. Detect YouTube / YouTube Music
        if (artist != null && (artist.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase) || artist.EndsWith("Topic", StringComparison.OrdinalIgnoreCase)))
        {
            return "YouTube Music";
        }
        if ((title != null && title.Contains("YouTube", StringComparison.OrdinalIgnoreCase)) ||
            (artist != null && artist.Contains("YouTube", StringComparison.OrdinalIgnoreCase)))
        {
            return "YouTube";
        }

        if (string.IsNullOrWhiteSpace(appId)) return "Media Player";

        string lower = appId.ToLowerInvariant();

        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("vlc")) return "VLC Media Player";
        if (lower.Contains("applemusic") || lower.Contains("itunes")) return "Apple Music";
        if (lower.Contains("tidal")) return "TIDAL";
        if (lower.Contains("foobar2000")) return "foobar2000";
        if (lower.Contains("aimp")) return "AIMP";
        if (lower.Contains("zunemusic") || lower.Contains("microsoft.zunemusic")) return "Groove Music";
        if (lower.Contains("microsoft.media.player")) return "Media Player";
        if (lower.Contains("zen")) return "Zen Browser";

        // Known browsers
        if (lower.Contains("edge") || lower.Contains("msedge")) return "Microsoft Edge";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("brave")) return "Brave";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("opera")) return "Opera";

        // 2. Query Windows Registry AppUserModelId (e.g. ZenToast-F0DC299D809B9700 or PWAs)
        string? regName = TryResolveFromRegistry(appId);
        if (!string.IsNullOrWhiteSpace(regName))
        {
            return regName;
        }

        // 3. Prevent raw hex hash names (e.g. "F0DC299D809B9700") from displaying
        if (IsHexOrHash(appId))
        {
            return "Web Browser";
        }

        string name = System.IO.Path.GetFileNameWithoutExtension(appId);
        int bang = name.IndexOf('!');
        if (bang >= 0 && bang < name.Length - 1)
        {
            name = name.Substring(bang + 1);
        }

        if (IsHexOrHash(name))
        {
            return "Web Browser";
        }

        return name;
    }

    private static bool IsHexOrHash(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return false;
        string s = str.Trim();
        if (s.Length >= 8 && s.Length <= 64)
        {
            bool allHex = true;
            foreach (char c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    allHex = false;
                    break;
                }
            }
            if (allHex) return true;
        }
        return false;
    }

    private static string? TryResolveFromRegistry(string appId)
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId");
            if (root != null)
            {
                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    if (subKeyName.IndexOf(appId, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        appId.IndexOf(subKeyName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        using var subKey = root.OpenSubKey(subKeyName);
                        var disp = subKey?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrWhiteSpace(disp)) return disp;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    [RelayCommand]
    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession == null && _manager != null)
        {
            try { _currentSession = _manager.GetCurrentSession(); } catch { }
        }

        if (_currentSession != null)
        {
            try
            {
                IsPlaying = !IsPlaying;
                await _currentSession.TryTogglePlayPauseAsync();
                UpdatePlaybackInfo();
            }
            catch { }
        }
    }

    [RelayCommand]
    public async Task SkipNextAsync()
    {
        if (_currentSession == null && _manager != null)
        {
            try { _currentSession = _manager.GetCurrentSession(); } catch { }
        }

        if (_currentSession != null)
        {
            try
            {
                await _currentSession.TrySkipNextAsync();
            }
            catch { }
        }
    }

    [RelayCommand]
    public async Task SkipPreviousAsync()
    {
        if (_currentSession == null && _manager != null)
        {
            try { _currentSession = _manager.GetCurrentSession(); } catch { }
        }

        if (_currentSession != null)
        {
            try
            {
                await _currentSession.TrySkipPreviousAsync();
            }
            catch { }
        }
    }

    public override void Pause()
    {
        _isHubVisible = false;
    }

    public override void Resume()
    {
        _isHubVisible = true;
        _ = RefreshSessionAsync();
    }

    public void Receive(HubVisibilityChangedMessage message)
    {
        if (message.IsVisible)
        {
            Resume();
        }
        else
        {
            Pause();
        }
    }
}
