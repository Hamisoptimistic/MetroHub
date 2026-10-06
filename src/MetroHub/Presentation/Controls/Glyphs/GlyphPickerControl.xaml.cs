using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Lean, tactile icon picker strip displaying curated standard Fluent vector icons.
/// </summary>
public partial class GlyphPickerControl : UserControl
{
    private bool _isUpdatingInternally;
    private readonly List<IconTileItem> _icons =
    [
        new("Desktop", SymbolRegular.Desktop24),
        new("Work", SymbolRegular.Briefcase24),
        new("Code", SymbolRegular.Code24),
        new("Rocket", SymbolRegular.Rocket24),
        new("Target", SymbolRegular.TargetArrow24),
        new("Study", SymbolRegular.Book24),
        new("Health", SymbolRegular.Heart24),
        new("Music", SymbolRegular.Headphones24),
        new("Gaming", SymbolRegular.Games24),
        new("Files", SymbolRegular.Folder24)
    ];

    public static readonly DependencyProperty SelectedGlyphProperty =
        DependencyProperty.Register(
            nameof(SelectedGlyph),
            typeof(string),
            typeof(GlyphPickerControl),
            new FrameworkPropertyMetadata("Desktop24", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedGlyphChanged));

    public string SelectedGlyph
    {
        get => (string)GetValue(SelectedGlyphProperty);
        set => SetValue(SelectedGlyphProperty, value);
    }

    public event EventHandler<string>? SelectedGlyphChanged;

    public GlyphPickerControl()
    {
        InitializeComponent();
        IconsItemsControl.ItemsSource = _icons;
        ApplySelectedGlyphToUi(SelectedGlyph);
        Loaded += (_, _) => ApplySelectedGlyphToUi(SelectedGlyph);
    }

    private static void OnSelectedGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GlyphPickerControl picker && e.NewValue is string newGlyph)
        {
            picker.ApplySelectedGlyphToUi(newGlyph);
        }
    }

    private void ApplySelectedGlyphToUi(string? glyph)
    {
        if (_isUpdatingInternally) return;

        string current = string.IsNullOrWhiteSpace(glyph) ? "Desktop24" : glyph;
        _isUpdatingInternally = true;

        try
        {
            var matchingIcon = _icons.FirstOrDefault(i =>
                string.Equals(i.Glyph, current, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.Name, current, StringComparison.OrdinalIgnoreCase));

            if (matchingIcon != null)
            {
                foreach (var item in _icons)
                {
                    item.IsSelected = (item == matchingIcon);
                }
            }
            else
            {
                foreach (var item in _icons)
                {
                    item.IsSelected = false;
                }
            }
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }

    private void OnIconTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var targetItem = (btn.Tag as IconTileItem) ?? (btn.DataContext as IconTileItem);
        if (targetItem == null) return;

        _isUpdatingInternally = true;
        try
        {
            foreach (var item in _icons)
            {
                item.IsSelected = (item == targetItem);
            }
            SelectedGlyph = targetItem.Glyph;
            SelectedGlyphChanged?.Invoke(this, targetItem.Glyph);
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }
}

/// <summary>
/// Data model for curated Fluent icon button tiles in the picker strip.
/// </summary>
public sealed class IconTileItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; }
    public SymbolRegular Symbol { get; }
    public string Glyph { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IconTileItem(string name, SymbolRegular symbol)
    {
        Name = name;
        Symbol = symbol;
        Glyph = symbol.ToString();
    }
}
