using System.Windows.Media;

namespace MetroHub.Presentation.Themes;

/// <summary>
/// Centralized C# access to semantic status colors and brushes matching Tokens.xaml.
/// Eliminates hardcoded hex values in C# code-behind files.
/// </summary>
public static class ThemeTokens
{
    public static readonly Color StatusSuccessColor = Color.FromRgb(0x00, 0xE6, 0x76);
    public static readonly Color StatusWarningColor = Color.FromRgb(0xFF, 0xB9, 0x00);
    public static readonly Color StatusDangerColor = Color.FromRgb(0xFF, 0x45, 0x3A);
    public static readonly Color StatusErrorColor = Color.FromRgb(0xFF, 0x6B, 0x6B);
    public static readonly Color StatusInfoColor = Color.FromRgb(0x60, 0xCD, 0xFF);

    public static readonly SolidColorBrush StatusSuccessBrush = CreateFrozenBrush(StatusSuccessColor);
    public static readonly SolidColorBrush StatusWarningBrush = CreateFrozenBrush(StatusWarningColor);
    public static readonly SolidColorBrush StatusDangerBrush = CreateFrozenBrush(StatusDangerColor);
    public static readonly SolidColorBrush StatusErrorBrush = CreateFrozenBrush(StatusErrorColor);
    public static readonly SolidColorBrush StatusInfoBrush = CreateFrozenBrush(StatusInfoColor);

    // Pre-frozen subtle tinted background for drop targets and overlays
    public static readonly SolidColorBrush StatusDangerSubtleBrush = CreateFrozenBrush(Color.FromArgb(45, StatusDangerColor.R, StatusDangerColor.G, StatusDangerColor.B));

    // Fluent 2 Central Accent Tokens (dynamically updated by SystemAccentColorService)
    public static Color AccentPrimaryColor { get; internal set; } = Color.FromRgb(0x4C, 0xC2, 0xFF);
    public static Color AccentSecondaryColor { get; internal set; } = Color.FromRgb(0x60, 0xCD, 0xFF);
    public static SolidColorBrush AccentPrimaryBrush { get; internal set; } = CreateFrozenBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
    // Typography Brushes (2-Tier System: Pure White + Luminous Icy Off-White)
    public static readonly SolidColorBrush TextPrimaryBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush TextSecondaryBrush = CreateFrozenBrush(Color.FromRgb(0xD8, 0xE2, 0xEC));
    public static readonly SolidColorBrush TextMutedBrush = TextSecondaryBrush;
    public static readonly SolidColorBrush TextSubtleBrush = TextSecondaryBrush;
    public static readonly SolidColorBrush MenuIconForegroundBrush = CreateFrozenBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));

    public static SolidColorBrush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
