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
public class CatalogItemModel : INotifyPropertyChanged
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

    [System.Text.Json.Serialization.JsonIgnore]
    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && !_isIconLoading && !string.IsNullOrWhiteSpace(_targetPath))
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
                string? cached = IconExtractorService.ExtractAndCacheIcon(_targetPath);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    App.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        try
                        {
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.UriSource = new Uri(cached, UriKind.Absolute);
                            bi.CacheOption = BitmapCacheOption.OnDemand;
                            bi.DecodePixelWidth = 24; // Lightweight 24px menu thumbnail
                            bi.EndInit();
                            bi.Freeze();
                            Icon = bi;
                        }
                        catch { }
                    });
                }
            }
            catch { }
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
