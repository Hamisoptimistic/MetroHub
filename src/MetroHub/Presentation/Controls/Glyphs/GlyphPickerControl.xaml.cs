using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Universal dual-mode picker control supporting categorized Fluent symbols and Unicode emojis/characters.
/// Encapsulates segmented mode switching, curated grids, and live custom input.
/// </summary>
public partial class GlyphPickerControl : UserControl
{
    private bool _isUpdatingInternally;

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
        FluentListBox.ItemsSource = GlyphCatalog.FluentIcons;
        EmojiListBox.ItemsSource = GlyphCatalog.Emojis;
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
            var matchingFluent = GlyphCatalog.FluentIcons.FirstOrDefault(i =>
                string.Equals(i.Glyph, current, StringComparison.OrdinalIgnoreCase));

            if (matchingFluent != null)
            {
                FluentTabRadio.IsChecked = true;
                FluentViewHost.Visibility = Visibility.Visible;
                EmojiViewHost.Visibility = Visibility.Collapsed;
                FluentListBox.SelectedItem = matchingFluent;
                EmojiListBox.SelectedItem = null;
                CustomGlyphInput.Text = string.Empty;
                return;
            }

            var matchingEmoji = GlyphCatalog.Emojis.FirstOrDefault(e =>
                string.Equals(e.Glyph, current, StringComparison.Ordinal));

            if (matchingEmoji != null)
            {
                EmojiTabRadio.IsChecked = true;
                FluentViewHost.Visibility = Visibility.Collapsed;
                EmojiViewHost.Visibility = Visibility.Visible;
                EmojiListBox.SelectedItem = matchingEmoji;
                FluentListBox.SelectedItem = null;
                CustomGlyphInput.Text = current;
                return;
            }

            // Custom glyph or symbol
            if (GlyphCatalog.IsSymbol(current))
            {
                FluentTabRadio.IsChecked = true;
                FluentViewHost.Visibility = Visibility.Visible;
                EmojiViewHost.Visibility = Visibility.Collapsed;
                FluentListBox.SelectedItem = null;
                EmojiListBox.SelectedItem = null;
                CustomGlyphInput.Text = string.Empty;
            }
            else
            {
                EmojiTabRadio.IsChecked = true;
                FluentViewHost.Visibility = Visibility.Collapsed;
                EmojiViewHost.Visibility = Visibility.Visible;
                FluentListBox.SelectedItem = null;
                EmojiListBox.SelectedItem = null;
                CustomGlyphInput.Text = current;
            }
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }

    private void OnTabRadioChecked(object sender, RoutedEventArgs e)
    {
        if (FluentViewHost == null || EmojiViewHost == null) return;

        bool isFluent = FluentTabRadio.IsChecked == true;
        FluentViewHost.Visibility = isFluent ? Visibility.Visible : Visibility.Collapsed;
        EmojiViewHost.Visibility = isFluent ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnFluentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingInternally) return;

        if (FluentListBox.SelectedItem is GlyphOption option)
        {
            _isUpdatingInternally = true;
            try
            {
                EmojiListBox.SelectedItem = null;
                CustomGlyphInput.Text = string.Empty;
                SelectedGlyph = option.Glyph;
                SelectedGlyphChanged?.Invoke(this, option.Glyph);
            }
            finally
            {
                _isUpdatingInternally = false;
            }
        }
    }

    private void OnEmojiSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingInternally) return;

        if (EmojiListBox.SelectedItem is GlyphOption option)
        {
            _isUpdatingInternally = true;
            try
            {
                FluentListBox.SelectedItem = null;
                CustomGlyphInput.Text = option.Glyph;
                SelectedGlyph = option.Glyph;
                SelectedGlyphChanged?.Invoke(this, option.Glyph);
            }
            finally
            {
                _isUpdatingInternally = false;
            }
        }
    }

    private void OnCustomGlyphInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingInternally) return;

        string text = CustomGlyphInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        _isUpdatingInternally = true;
        try
        {
            FluentListBox.SelectedItem = null;
            EmojiListBox.SelectedItem = GlyphCatalog.Emojis.FirstOrDefault(i => i.Glyph == text);
            SelectedGlyph = text;
            SelectedGlyphChanged?.Invoke(this, text);
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }
}
