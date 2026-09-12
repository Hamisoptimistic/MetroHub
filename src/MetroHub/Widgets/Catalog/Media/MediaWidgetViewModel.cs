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
using System.Windows.Threading;
using MetroHub.Widgets.Serialization;

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

    private bool _isSettingsLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStaticGlowVisible))]
    [NotifyPropertyChangedFor(nameof(IsAnimatedGlowVisible))]
    [NotifyPropertyChangedFor(nameof(IsGlowAnimated))]
    private MediaGlowMode _glowMode = MediaGlowMode.Static;

    partial void OnGlowModeChanged(MediaGlowMode value)
    {
        if (_isSettingsLoaded)
        {
            SaveSettings();
        }
    }

    partial void OnHasThumbnailChanged(bool value)
    {
        OnPropertyChanged(nameof(IsStaticGlowVisible));
        OnPropertyChanged(nameof(IsAnimatedGlowVisible));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGlowAnimated));
    }

    public bool IsStaticGlowVisible => GlowMode == MediaGlowMode.Static && HasThumbnail;

    public bool IsAnimatedGlowVisible => GlowMode == MediaGlowMode.Animated && HasThumbnail;

    public bool IsGlowAnimated => GlowMode == MediaGlowMode.Animated && IsPlaying && _isHubVisible;

    [ObservableProperty]
    private Brush? _glowBrush;

    [ObservableProperty]
    private Brush? _sensualRadialBrush;

    [ObservableProperty]
    private Brush? _fluidWaveBrush;

    [ObservableProperty]
    private Brush? _fluidSecondaryBrush;

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

    [ObservableProperty]
    private bool _canSeek = true;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private double _progressRatio;

    private DispatcherTimer? _playbackTimer;
    private long _lastLocalTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private TimeSpan _lastTimelinePosition = TimeSpan.Zero;
    private TimeSpan _trackDuration = TimeSpan.Zero;
    private bool _isScrubbing = false;

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

        LoadSettings(model.SettingsJson);
        _isSettingsLoaded = true;

        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _playbackTimer.Tick += OnPlaybackTimerTick;

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
            _currentSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
        }

        _currentSession = newSession;

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
            await UpdateMediaDetailsAsync();
            UpdateTimelineInfo();
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
                FluidWaveBrush = null;
                FluidSecondaryBrush = null;
                GlowColor = Color.FromRgb(0x3A, 0x82, 0xD4);
                var fallbackBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x82, 0xD4));
                fallbackBrush.Freeze();
                GlowSolidBrush = fallbackBrush;
                IsPlaying = false;
                DurationSeconds = 0;
                PositionSeconds = 0;
                ProgressRatio = 0.0;
                _playbackTimer?.Stop();
            });
        }
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = UpdateMediaDetailsAsync();
        UpdateTimelineInfo();
    }

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        UpdatePlaybackInfo();
        UpdateTimelineInfo();
    }

    private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        UpdateTimelineInfo();
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
                    CanSeek = controls?.IsPlaybackPositionEnabled ?? true;

                    if (playing && HasMedia && _isHubVisible)
                    {
                        _playbackTimer?.Start();
                    }
                    else
                    {
                        _playbackTimer?.Stop();
                    }
                });
            }
        }
        catch { }
    }

    private void UpdateTimelineInfo()
    {
        if (_currentSession == null) return;

        try
        {
            var timeline = _currentSession.GetTimelineProperties();
            if (timeline != null)
            {
                var newDuration = timeline.EndTime - timeline.StartTime;
                if (newDuration <= TimeSpan.Zero && timeline.MaxSeekTime > timeline.MinSeekTime)
                {
                    newDuration = timeline.MaxSeekTime - timeline.MinSeekTime;
                }

                if (newDuration > TimeSpan.Zero)
                {
                    _trackDuration = newDuration;
                }

                if (timeline.Position >= TimeSpan.Zero)
                {
                    _lastTimelinePosition = timeline.Position;
                    _lastLocalTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                }

                double durationSec = Math.Max(0, _trackDuration.TotalSeconds);
                double positionSec = Math.Max(0, _lastTimelinePosition.TotalSeconds);

                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (durationSec > 0)
                    {
                        DurationSeconds = durationSec;
                    }

                    if (!_isScrubbing && DurationSeconds > 0)
                    {
                        PositionSeconds = Math.Clamp(positionSec, 0, DurationSeconds);
                        ProgressRatio = Math.Clamp(PositionSeconds / DurationSeconds, 0.0, 1.0);
                    }

                    if (IsPlaying && HasMedia && _isHubVisible)
                    {
                        _playbackTimer?.Start();
                    }
                    else
                    {
                        _playbackTimer?.Stop();
                    }
                });
            }
        }
        catch { }
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (!_isHubVisible || !HasMedia || !IsPlaying || _isScrubbing) return;

        if (DurationSeconds <= 0 || _trackDuration <= TimeSpan.Zero)
        {
            UpdateTimelineInfo();
            return;
        }

        double elapsedSeconds = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _lastLocalTimestamp) / System.Diagnostics.Stopwatch.Frequency;
        if (elapsedSeconds < 0) elapsedSeconds = 0;

        double currentPos = Math.Clamp(_lastTimelinePosition.TotalSeconds + elapsedSeconds, 0, DurationSeconds);
        PositionSeconds = currentPos;
        ProgressRatio = DurationSeconds > 0 ? Math.Clamp(currentPos / DurationSeconds, 0.0, 1.0) : 0.0;
    }

    public void StartScrubbing()
    {
        _isScrubbing = true;
    }

    public void StopScrubbing(double finalRatio)
    {
        _isScrubbing = false;
        _ = SeekToRatioAsync(finalRatio);
    }

    public async Task SeekToRatioAsync(double ratio)
    {
        if (_currentSession == null && _manager != null)
        {
            try { _currentSession = _manager.GetCurrentSession(); } catch { }
        }

        if (_currentSession == null || DurationSeconds <= 0) return;

        ratio = Math.Clamp(ratio, 0.0, 1.0);
        double targetSeconds = ratio * DurationSeconds;
        long requestedTicks = (long)(targetSeconds * TimeSpan.TicksPerSecond);

        PositionSeconds = targetSeconds;
        ProgressRatio = ratio;
        _lastTimelinePosition = TimeSpan.FromSeconds(targetSeconds);
        _lastLocalTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            await _currentSession.TryChangePlaybackPositionAsync(requestedTicks);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaWidget] Seek failed: {ex.Message}");
        }
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
            RadialGradientBrush? fluidWaveBrush = null;
            LinearGradientBrush? fluidSecondaryBrush = null;

            if (props.Thumbnail != null)
            {
                bmp = await LoadThumbnailAsync(props.Thumbnail);
                if (bmp is BitmapSource bs)
                {
                    (glowColor, glowSolidBrush, sensualRadialBrush, fluidWaveBrush, fluidSecondaryBrush) = CreateGlow(bs);
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
                FluidWaveBrush = fluidWaveBrush;
                FluidSecondaryBrush = fluidSecondaryBrush;
                GlowBrush = sensualRadialBrush;
                GlowColor = glowColor;
                IsPlaying = playing;
                HasMedia = true;
                CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? true;
                CanSkipNext = controls?.IsNextEnabled ?? true;
                CanSkipPrevious = controls?.IsPreviousEnabled ?? true;
            });

            _ = Task.Run(async () =>
            {
                UpdateTimelineInfo();
                await Task.Delay(200);
                UpdateTimelineInfo();
                await Task.Delay(400);
                UpdateTimelineInfo();
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

    private static (Color, SolidColorBrush, RadialGradientBrush, RadialGradientBrush, LinearGradientBrush) CreateGlow(BitmapSource bitmap)
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
                s = Math.Clamp(s * 1.40, 0.52, 0.98);
                v = Math.Clamp(v * 1.20, 0.62, 0.96);
                accent = ColorFromHsv(h, s, v);
            }
            else
            {
                double avgLum = validPixelCount > 0 ? totalLum / validPixelCount : 0.8;
                if (avgLum > 0.4)
                {
                    accent = Color.FromRgb(242, 246, 255);
                }
                else
                {
                    accent = Color.FromRgb(215, 228, 245);
                }
            }

            var solidBrush = new SolidColorBrush(accent);
            solidBrush.Freeze();

            // 1. Sensual Static Brush: EXACT classic localized halo behind the album art
            // Confined to the right side of the card without covering the entire tile!
            var sensualBrush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                Center = new Point(0.80, 0.42),
                GradientOrigin = new Point(0.80, 0.42),
                RadiusX = 0.95,
                RadiusY = 1.15
            };
            AddSmoothCosineStops(sensualBrush.GradientStops, accent, isMonochrome ? (byte)100 : (byte)140, 0, 16);
            sensualBrush.Freeze();

            // 2. Fluid Wave Stream Brush: Expansive radial stream emanating from album art in expanded overscan coords
            // and washing across toward the left, covering the entire tile with zero boundary cliff
            var fluidWaveBrush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                Center = new Point(0.656, 0.470),
                GradientOrigin = new Point(0.656, 0.470),
                RadiusX = 1.35,
                RadiusY = 1.15
            };
            AddSmoothCosineStops(fluidWaveBrush.GradientStops, accent, isMonochrome ? (byte)150 : (byte)215, 0, 16);
            fluidWaveBrush.Freeze();

            // 3. Fluid Secondary Undercurrent Brush: Horizontal linear gradient flowing smoothly right to left
            var fluidSecondaryBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0.85, 0.35),
                EndPoint = new Point(0.08, 0.65)
            };
            AddSmoothCosineStops(fluidSecondaryBrush.GradientStops, accent, isMonochrome ? (byte)95 : (byte)140, 0, 14);
            fluidSecondaryBrush.Freeze();

            return (accent, solidBrush, sensualBrush, fluidWaveBrush, fluidSecondaryBrush);
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
            AddSmoothCosineStops(fallbackSensual.GradientStops, fallbackColor, 140, 0, 16);
            fallbackSensual.Freeze();

            var fallbackWave = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                Center = new Point(0.656, 0.470),
                GradientOrigin = new Point(0.656, 0.470),
                RadiusX = 1.35,
                RadiusY = 1.15
            };
            AddSmoothCosineStops(fallbackWave.GradientStops, fallbackColor, 215, 0, 16);
            fallbackWave.Freeze();

            var fallbackSecondary = new LinearGradientBrush
            {
                StartPoint = new Point(0.85, 0.35),
                EndPoint = new Point(0.08, 0.65)
            };
            AddSmoothCosineStops(fallbackSecondary.GradientStops, fallbackColor, 140, 0, 14);
            fallbackSecondary.Freeze();

            return (fallbackColor, fallbackSolid, fallbackSensual, fallbackWave, fallbackSecondary);
        }
    }

    public static ImageSource DitherNoiseTexture { get; } = CreateDitherNoiseBitmap();

    private static ImageSource CreateDitherNoiseBitmap()
    {
        int width = 64;
        int height = 64;
        byte[] pixels = new byte[width * height * 4];
        var rand = new Random(1337);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte v = (byte)rand.Next(0, 256);
            pixels[i] = v;     // B
            pixels[i + 1] = v; // G
            pixels[i + 2] = v; // R
            pixels[i + 3] = (byte)rand.Next(10, 26); // subtle ~4-10% dither alpha
        }

        var bitmap = BitmapSource.Create(
            width, height,
            96, 96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4
        );
        bitmap.Freeze();
        return bitmap;
    }

    public ImageSource DitherNoise => DitherNoiseTexture;

    private static void AddSmoothCosineStops(GradientStopCollection collection, Color color, byte maxAlpha, byte minAlpha, int stopCount = 16)
    {
        for (int i = 0; i <= stopCount; i++)
        {
            double t = (double)i / stopCount;
            // Cosine smooth curve with zero derivative at both ends (C^1 continuous, eliminating Mach bands)
            double factor = (1.0 + Math.Cos(Math.PI * t)) / 2.0;
            byte a = (byte)Math.Round(minAlpha + (maxAlpha - minAlpha) * factor);
            collection.Add(new GradientStop(Color.FromArgb(a, color.R, color.G, color.B), t));
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
        _playbackTimer?.Stop();
        OnPropertyChanged(nameof(IsGlowAnimated));
    }

    public override void Resume()
    {
        _isHubVisible = true;
        OnPropertyChanged(nameof(IsGlowAnimated));
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

    protected override void LoadSettings(string? settingsJson)
    {
        var settings = WidgetSerializer.Deserialize<MediaWidgetSettings>(settingsJson);
        if (settings != null)
        {
            GlowMode = settings.GlowMode;
        }
    }

    public override void SaveSettings()
    {
        var settings = new MediaWidgetSettings
        {
            GlowMode = GlowMode
        };
        Model.TargetPath = "media";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
        MainWindow.Current?.SaveGroupsAndLayout();
    }

    public void SetGlowMode(MediaGlowMode mode)
    {
        GlowMode = mode;
        SaveSettings();
    }
}
