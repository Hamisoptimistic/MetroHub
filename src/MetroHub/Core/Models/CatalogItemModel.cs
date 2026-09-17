using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Core.Services;

namespace MetroHub.Core.Models;

/// <summary>
/// Modular catalog item model representing an application, shortcut, tool, or folder.
/// Designed for reuse across Apps, Widgets, Bookmarks, and Tool catalogs.
/// Supports lazy, non-blocking icon loading for high performance and low RAM usage.
/// </summary>
public sealed class CatalogItemModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _targetPath = string.Empty;
    private string? _arguments;
    private TileType _tileType = TileType.App;
    private string? _category;
    private ImageSource? _icon;
    private bool _isIconLoading = false;

    private int _spanX = 2;
    private int _spanY = 2;
    private string? _providerId;
    private object? _tag;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string TargetPath
    {
        get => _targetPath;
        set => SetField(ref _targetPath, value);
    }

    public string? Arguments
    {
        get => _arguments;
        set => SetField(ref _arguments, value);
    }

    public TileType TileType
    {
        get => _tileType;
        set => SetField(ref _tileType, value);
    }

    public string? Category
    {
        get => _category;
        set => SetField(ref _category, value);
    }

    public int SpanX
    {
        get => _spanX;
        set => SetField(ref _spanX, value);
    }

    public int SpanY
    {
        get => _spanY;
        set => SetField(ref _spanY, value);
    }

    public string? ProviderId
    {
        get => _providerId;
        set => SetField(ref _providerId, value);
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public object? Tag
    {
        get => _tag;
        set => SetField(ref _tag, value);
    }

    private const int MaxCachedIcons = 64;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> _memoryIconCache = new(StringComparer.OrdinalIgnoreCase);

    public static void ClearMemoryCache()
    {
        _memoryIconCache.Clear();
    }

    public static bool HasMemoryCachedIcon(string targetPath)
    {
        return !string.IsNullOrWhiteSpace(targetPath) && _memoryIconCache.ContainsKey(targetPath);
    }

    public static void PrewarmMemoryCache(IEnumerable<CatalogItemModel> items, int maxItems = 24)
    {
        if (items == null) return;
        Task.Run(() =>
        {
            var itemList = items.Where(i => !string.IsNullOrWhiteSpace(i.TargetPath)).Take(maxItems).ToList();
            if (itemList.Count == 0) return;

            Parallel.ForEach(itemList, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 4) }, item =>
            {
                try
                {
                    if (item.TargetPath == null) return;

                    if (_memoryIconCache.TryGetValue(item.TargetPath, out var memImg))
                    {
                        if (item._icon != memImg)
                        {
                            item._icon = memImg;
                            App.Current?.Dispatcher?.InvokeAsync(() => item.OnPropertyChanged(nameof(Icon)),
                                System.Windows.Threading.DispatcherPriority.Background);
                        }
                        return;
                    }

                    string? cached = IconExtractorService.ExtractAndCacheIcon(item.TargetPath);
                    if (!string.IsNullOrWhiteSpace(cached))
                    {
                        var img = GetOrCreateBitmapImage(cached, item.TargetPath);
                        if (img != null && item._icon != img)
                        {
                            item._icon = img;
                            App.Current?.Dispatcher?.InvokeAsync(() => item.OnPropertyChanged(nameof(Icon)),
                                System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }
                }
                catch { }
            });
        });
    }

    private static ImageSource? GetOrCreateBitmapImage(string cachedPath, string targetPath)
    {
        if (_memoryIconCache.TryGetValue(targetPath, out var existing))
        {
            return existing;
        }

        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bi.UriSource = new Uri(cachedPath, UriKind.Absolute);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 24; // Lightweight 24px menu thumbnail (<2.5 KB RAM per app)
            bi.EndInit();
            bi.Freeze();

            if (_memoryIconCache.Count >= MaxCachedIcons)
            {
                var keysToRemove = _memoryIconCache.Keys.Take(MaxCachedIcons / 5).ToList();
                foreach (var key in keysToRemove)
                {
                    _memoryIconCache.TryRemove(key, out _);
                }
            }

            _memoryIconCache[targetPath] = bi;
            return bi;
        }
        catch
        {
            return null;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public ImageSource? Icon
    {
        get
        {
            if (_icon != null) return _icon;

            if (!string.IsNullOrWhiteSpace(_targetPath) && _memoryIconCache.TryGetValue(_targetPath, out var memImg))
            {
                _icon = memImg;
                return _icon;
            }

            if (!_isIconLoading && !string.IsNullOrWhiteSpace(_targetPath))
            {
                _isIconLoading = true;
                LoadIconInBackground();
            }
            return _icon;
        }
        set => SetField(ref _icon, value);
    }

    private void LoadIconInBackground()
    {
        Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_targetPath)) return;

                if (_memoryIconCache.TryGetValue(_targetPath, out var memImg))
                {
                    App.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        Icon = memImg;
                    }, System.Windows.Threading.DispatcherPriority.Normal);
                    return;
                }

                string? cached = IconExtractorService.ExtractAndCacheIcon(_targetPath);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    var bi = GetOrCreateBitmapImage(cached, _targetPath);
                    if (bi != null)
                    {
                        App.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            Icon = bi;
                        }, System.Windows.Threading.DispatcherPriority.Normal);
                    }
                }
            }
            catch { }
            finally
            {
                _isIconLoading = false;
            }
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
