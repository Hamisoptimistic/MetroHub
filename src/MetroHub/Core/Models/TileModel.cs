using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace MetroHub.Core.Models;

public enum TileType
{
    App,
    WebUrl,
    Widget,
    Folder
}

public class TileModel : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _title = string.Empty;
    private string _targetPath = string.Empty;
    private string? _arguments;
    private string? _iconPath;
    private TileType _tileType = TileType.App;
    private int _spanX = 2; // Default Medium (2x2)
    private int _spanY = 2;
    private string? _sectionHeader;
    private string? _group;
    private string? _accentColor;
    private int _orderIndex = 0;
    private double _x = 0;
    private double _y = 0;
    private int _col = 0;
    private int _row = 0;

    public int Col
    {
        get => _col;
        set => SetField(ref _col, value);
    }

    public int Row
    {
        get => _row;
        set => SetField(ref _row, value);
    }

    public double X
    {
        get => _x;
        set => SetField(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => SetField(ref _y, value);
    }
    private bool _runAsAdmin = false;
    private bool _isBeingDragged = false;

    [JsonIgnore]
    public bool IsBeingDragged
    {
        get => _isBeingDragged;
        set => SetField(ref _isBeingDragged, value);
    }

    private bool _isLocked = false;

    public bool IsLocked
    {
        get => _isLocked;
        set => SetField(ref _isLocked, value);
    }

    private bool _isSelected = false;

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string? SectionHeader
    {
        get => _sectionHeader;
        set => SetField(ref _sectionHeader, value);
    }

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
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

    public string? IconPath
    {
        get => _iconPath;
        set => SetField(ref _iconPath, value);
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TileType TileType
    {
        get => _tileType;
        set
        {
            if (SetField(ref _tileType, value))
            {
                OnPropertyChanged(nameof(TileContent));
            }
        }
    }

    [JsonIgnore]
    private object? _widgetViewModel;

    [JsonIgnore]
    public object? WidgetViewModel
    {
        get => _widgetViewModel;
        set
        {
            if (SetField(ref _widgetViewModel, value))
            {
                OnPropertyChanged(nameof(TileContent));
            }
        }
    }

    [JsonIgnore]
    private object? _stubWidgetViewModel;

    [JsonIgnore]
    public object? TileContent
    {
        get
        {
            if (TileType == TileType.Widget)
            {
                return _widgetViewModel ?? (_stubWidgetViewModel ??= MetroHub.Widgets.Registry.WidgetRegistry.CreateViewModelForTile(this));
            }
            return this;
        }
    }

    public int SpanX
    {
        get => _spanX;
        set
        {
            if (SetField(ref _spanX, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(WidthPixels));
                OnPropertyChanged(nameof(IsSmall));
                OnPropertyChanged(nameof(IsMedium));
                OnPropertyChanged(nameof(IsWide));
            }
        }
    }

    public int SpanY
    {
        get => _spanY;
        set
        {
            if (SetField(ref _spanY, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(HeightPixels));
                OnPropertyChanged(nameof(IsSmall));
                OnPropertyChanged(nameof(IsMedium));
                OnPropertyChanged(nameof(IsWide));
            }
        }
    }

    public string? Group
    {
        get => _group;
        set => SetField(ref _group, value);
    }

    public string? AccentColor
    {
        get => _accentColor;
        set => SetField(ref _accentColor, value);
    }

    private string _tileStyle = "Default";
    public string TileStyle
    {
        get => _tileStyle;
        set => SetField(ref _tileStyle, value);
    }

    private string? _settingsJson;
    public string? SettingsJson
    {
        get => _settingsJson;
        set => SetField(ref _settingsJson, value);
    }

    public int OrderIndex
    {
        get => _orderIndex;
        set => SetField(ref _orderIndex, value);
    }

    public bool RunAsAdmin
    {
        get => _runAsAdmin;
        set => SetField(ref _runAsAdmin, value);
    }

    [JsonIgnore]
    public bool IsSmall => SpanX == 1 && SpanY == 1;

    [JsonIgnore]
    public bool IsMedium => SpanX == 2 && SpanY == 2;

    [JsonIgnore]
    public bool IsWide => SpanX >= 4 && SpanY == 2;

    // Unit size: 56px base + 8px gap = 64px grid step
    [JsonIgnore]
    public double WidthPixels => (SpanX * 64) - 8;

    [JsonIgnore]
    public double HeightPixels => (SpanY * 64) - 8;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
