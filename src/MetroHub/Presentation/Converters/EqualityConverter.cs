using System;
using System.Globalization;
using System.Windows.Data;

namespace MetroHub.Presentation.Converters;

/// <summary>
/// Compares multiple bound values for equality.
/// Enables centralized DataTriggers comparing ViewModel selection properties against control CommandParameters.
/// </summary>
public class EqualityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return false;
        if (values[0] == null && values[1] == null) return true;
        if (values[0] == null || values[1] == null) return false;
        return string.Equals(values[0].ToString(), values[1].ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
