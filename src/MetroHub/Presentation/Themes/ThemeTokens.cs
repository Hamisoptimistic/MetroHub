using System.Windows.Media;

namespace MetroHub.Presentation.Themes;

/// <summary>
/// Centralized C# access to semantic status colors and brushes matching Tokens.xaml.
/// Eliminates hardcoded hex values in C# code-behind files.
/// </summary>
public static class ThemeTokens
{
    public static readonly Color StatusSuccessColor = Color.FromRgb(0x4E, 0xCA, 0x78);
    public static readonly Color StatusWarningColor = Color.FromRgb(0xFF, 0xB9, 0x00);
    public static readonly Color StatusDangerColor = Color.FromRgb(0xFF, 0x45, 0x3A);
    public static readonly Color StatusErrorColor = Color.FromRgb(0xFF, 0x6B, 0x6B);

    public static readonly SolidColorBrush StatusSuccessBrush = CreateFrozenBrush(StatusSuccessColor);
    public static readonly SolidColorBrush StatusWarningBrush = CreateFrozenBrush(StatusWarningColor);
    public static readonly SolidColorBrush StatusDangerBrush = CreateFrozenBrush(StatusDangerColor);
    public static readonly SolidColorBrush StatusErrorBrush = CreateFrozenBrush(StatusErrorColor);

    public static SolidColorBrush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
