using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MetroHub.Widgets.Catalog.Clock;

/// <summary>
/// Micro-motion text presenter for Clock digits and dates (Tier 3 Micro-Polish & Motion).
/// Performs hardware-accelerated dual-layer slide-and-crossfade transitions when text changes.
/// Eliminates harsh number snapping and provides a fluid, luxury timepiece feel.
/// </summary>
public partial class AnimatedTimeBlock : UserControl
{
    private bool _isLoaded;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(AnimatedTimeBlock),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTextChangedStatic));

    public static readonly DependencyProperty TextAlignmentProperty =
        DependencyProperty.Register(
            nameof(TextAlignment),
            typeof(TextAlignment),
            typeof(AnimatedTimeBlock),
            new FrameworkPropertyMetadata(TextAlignment.Left, OnStylingChangedStatic));

    public static readonly DependencyProperty SlideDistanceProperty =
        DependencyProperty.Register(
            nameof(SlideDistance),
            typeof(double),
            typeof(AnimatedTimeBlock),
            new PropertyMetadata(8.0));

    public static readonly DependencyProperty DurationMsProperty =
        DependencyProperty.Register(
            nameof(DurationMs),
            typeof(int),
            typeof(AnimatedTimeBlock),
            new PropertyMetadata(220));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public double SlideDistance
    {
        get => (double)GetValue(SlideDistanceProperty);
        set => SetValue(SlideDistanceProperty, value);
    }

    public int DurationMs
    {
        get => (int)GetValue(DurationMsProperty);
        set => SetValue(DurationMsProperty, value);
    }

    public AnimatedTimeBlock()
    {
        InitializeComponent();

        SyncStyling();
        if (PartCurrentText != null)
        {
            PartCurrentText.Text = Text;
            PartCurrentText.Opacity = 1.0;
            PartCurrentTransform.Y = 0;
        }

        Loaded += OnControlLoaded;
    }

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        SyncStyling();

        PartCurrentText.Text = Text;
        PartCurrentText.Opacity = 1.0;
        PartCurrentTransform.Y = 0;

        PartPreviousText.Text = string.Empty;
        PartPreviousText.Opacity = 0.0;
        PartPreviousTransform.Y = 0;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == FontSizeProperty ||
            e.Property == FontWeightProperty ||
            e.Property == FontFamilyProperty ||
            e.Property == ForegroundProperty)
        {
            SyncStyling();
        }
    }

    private static void OnStylingChangedStatic(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnimatedTimeBlock block)
        {
            block.SyncStyling();
        }
    }

    private void SyncStyling()
    {
        if (PartPreviousText == null || PartCurrentText == null) return;

        PartPreviousText.FontSize = FontSize;
        PartCurrentText.FontSize = FontSize;

        PartPreviousText.FontWeight = FontWeight;
        PartCurrentText.FontWeight = FontWeight;

        PartPreviousText.FontFamily = FontFamily;
        PartCurrentText.FontFamily = FontFamily;

        PartPreviousText.Foreground = Foreground;
        PartCurrentText.Foreground = Foreground;

        PartPreviousText.TextAlignment = TextAlignment;
        PartCurrentText.TextAlignment = TextAlignment;
    }

    private static void OnTextChangedStatic(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnimatedTimeBlock block)
        {
            block.OnTextChanged((string?)e.OldValue ?? string.Empty, (string?)e.NewValue ?? string.Empty);
        }
    }

    private void OnTextChanged(string oldText, string newText)
    {
        if (PartCurrentText == null || PartPreviousText == null) return;

        // If not loaded or initializing from empty, show without animation
        if (!_isLoaded || string.IsNullOrEmpty(oldText))
        {
            PartCurrentText.Text = newText;
            PartCurrentText.Opacity = 1.0;
            PartCurrentTransform.Y = 0;
            PartPreviousText.Text = string.Empty;
            PartPreviousText.Opacity = 0.0;
            return;
        }

        if (oldText == newText) return;

        // Assign texts to layers
        PartPreviousText.Text = oldText;
        PartCurrentText.Text = newText;

        // Cancel previous animations
        PartPreviousText.BeginAnimation(UIElement.OpacityProperty, null);
        PartPreviousTransform.BeginAnimation(TranslateTransform.YProperty, null);
        PartCurrentText.BeginAnimation(UIElement.OpacityProperty, null);
        PartCurrentTransform.BeginAnimation(TranslateTransform.YProperty, null);

        double slide = SlideDistance;
        var duration = TimeSpan.FromMilliseconds(DurationMs);
        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        // Animate Previous: slides up and dissolves out
        var prevFade = new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = easeOut };
        var prevSlide = new DoubleAnimation(0.0, -slide, duration) { EasingFunction = easeOut };

        // Animate Current: slides in from bottom and fades in
        var currFade = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = easeOut };
        var currSlide = new DoubleAnimation(slide, 0.0, duration) { EasingFunction = easeOut };

        PartPreviousText.BeginAnimation(UIElement.OpacityProperty, prevFade);
        PartPreviousTransform.BeginAnimation(TranslateTransform.YProperty, prevSlide);

        PartCurrentText.BeginAnimation(UIElement.OpacityProperty, currFade);
        PartCurrentTransform.BeginAnimation(TranslateTransform.YProperty, currSlide);
    }
}
