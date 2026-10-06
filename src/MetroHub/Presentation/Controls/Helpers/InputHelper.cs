namespace MetroHub.Presentation.Controls;

using System.Windows;
using Wpf.Ui.Controls;

/// <summary>
/// Provides attached properties for standardizing MetroHub inputs (icons, layout helpers).
/// </summary>
public static class InputHelper
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached(
            "Icon",
            typeof(SymbolRegular?),
            typeof(InputHelper),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnIconChanged));

    public static readonly DependencyProperty HasIconProperty =
        DependencyProperty.RegisterAttached(
            "HasIcon",
            typeof(bool),
            typeof(InputHelper),
            new FrameworkPropertyMetadata(false));

    public static SymbolRegular? GetIcon(DependencyObject obj) =>
        (SymbolRegular?)obj.GetValue(IconProperty);

    public static void SetIcon(DependencyObject obj, SymbolRegular? value) =>
        obj.SetValue(IconProperty, value);

    public static bool GetHasIcon(DependencyObject obj) =>
        (bool)obj.GetValue(HasIconProperty);

    public static void SetHasIcon(DependencyObject obj, bool value) =>
        obj.SetValue(HasIconProperty, value);

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        SetHasIcon(d, e.NewValue is SymbolRegular);
    }
}
