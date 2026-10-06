using System;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Universal presenter control that dynamically resolves and renders either
/// a Fluent vector SymbolIcon or a Segoe UI Emoji/Unicode glyph.
/// </summary>
public partial class GlyphPresenter : UserControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(
            nameof(Glyph),
            typeof(string),
            typeof(GlyphPresenter),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnGlyphChanged));

    public static readonly DependencyProperty GlyphSizeProperty =
        DependencyProperty.Register(
            nameof(GlyphSize),
            typeof(double),
            typeof(GlyphPresenter),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public GlyphPresenter()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateVisual();
    }

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GlyphPresenter presenter)
        {
            presenter.UpdateVisual();
        }
    }

    private void UpdateVisual()
    {
        if (PART_Symbol == null || PART_Emoji == null) return;

        string? glyph = Glyph;
        if (string.IsNullOrWhiteSpace(glyph))
        {
            PART_Symbol.Visibility = Visibility.Collapsed;
            PART_Emoji.Visibility = Visibility.Collapsed;
            return;
        }

        if (Enum.TryParse<SymbolRegular>(glyph, true, out var symbol))
        {
            PART_Symbol.Symbol = symbol;
            PART_Symbol.Visibility = Visibility.Visible;
            PART_Emoji.Visibility = Visibility.Collapsed;
        }
        else
        {
            PART_Symbol.Visibility = Visibility.Collapsed;
            PART_Emoji.Text = glyph;
            PART_Emoji.Visibility = Visibility.Visible;
        }
    }
}
