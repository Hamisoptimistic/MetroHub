using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MetroHub.Presentation.Converters;

/// <summary>
/// Converts a hex color string (e.g. #2563EB) into a frozen WPF SolidColorBrush.
/// Keeps widget ViewModels free of WPF UI/Media dependencies.
/// </summary>
public class StringToBrushConverter : IValueConverter
{
    private static readonly BrushConverter _brushConverter = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var brush = _brushConverter.ConvertFromString(hex) as Brush;
                if (brush != null)
                {
                    if (brush.CanFreeze && !brush.IsFrozen) brush.Freeze();
                    return brush;
                }
            }
            catch { }
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
