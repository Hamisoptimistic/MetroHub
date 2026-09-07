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
