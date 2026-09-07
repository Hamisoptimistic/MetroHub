using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace MetroHub.Core.Models;

public class TileGroupModel : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _title = "New Group";
    private string? _headerColor = null;
    private int _col = 0;
    private int _row = 0;
    private double _x = 0;
    private double _y = 0;
    private bool _isEditing = false;
    private bool _isBeingDragged = false;
    private bool _isLocked = false;
    private int _columnIndex = 0;
    private int _orderIndex = 0;

    public int ColumnIndex
    {
        get => _columnIndex;
        set => SetField(ref _columnIndex, value);
    }

    public int OrderIndex
    {
        get => _orderIndex;
        set => SetField(ref _orderIndex, value);
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

    public string? HeaderColor
    {
        get => _headerColor;
        set => SetField(ref _headerColor, value);
    }

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

    [JsonIgnore]
    public bool IsEditing
    {
        get => _isEditing;
        set => SetField(ref _isEditing, value);
    }

    [JsonIgnore]
    public bool IsBeingDragged
    {
        get => _isBeingDragged;
        set => SetField(ref _isBeingDragged, value);
    }

    public bool IsLocked
    {
        get => _isLocked;
        set => SetField(ref _isLocked, value);
    }

    private string? _tintColor = null;
    public string? TintColor
    {
        get => _tintColor;
        set
        {
            if (SetField(ref _tintColor, value))
            {
                OnPropertyChanged(nameof(TintBackgroundBrush));
                OnPropertyChanged(nameof(TintBorderBrush));
                OnPropertyChanged(nameof(HasTint));
            }
        }
    }

    private double _plateWidth = 0;
    public double PlateWidth
    {
        get => _plateWidth;
        set => SetField(ref _plateWidth, value);
    }

    private double _plateHeight = 0;
    public double PlateHeight
    {
        get => _plateHeight;
        set => SetField(ref _plateHeight, value);
    }

    private double _plateX = 0;
    public double PlateX
    {
        get => _plateX;
        set => SetField(ref _plateX, value);
    }

    private double _plateY = 0;
    public double PlateY
    {
        get => _plateY;
        set => SetField(ref _plateY, value);
    }

    [JsonIgnore]
    public bool HasTint => !string.IsNullOrEmpty(_tintColor);

    [JsonIgnore]
    public System.Windows.Media.Brush TintBackgroundBrush
    {
        get
        {
            if (string.IsNullOrEmpty(_tintColor)) return System.Windows.Media.Brushes.Transparent;
            try
            {
                var col = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_tintColor);
                col.A = 32; // ~12% subtle translucent tint
                var brush = new System.Windows.Media.SolidColorBrush(col);
                brush.Freeze();
                return brush;
            }
            catch
            {
                return System.Windows.Media.Brushes.Transparent;
            }
        }
    }

    [JsonIgnore]
    public System.Windows.Media.Brush TintBorderBrush
    {
        get
        {
            if (string.IsNullOrEmpty(_tintColor)) return System.Windows.Media.Brushes.Transparent;
            try
            {
                var col = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_tintColor);
                col.A = 70; // ~28% subtle accent border
                var brush = new System.Windows.Media.SolidColorBrush(col);
                brush.Freeze();
                return brush;
            }
            catch
            {
                return System.Windows.Media.Brushes.Transparent;
            }
        }
    }

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
