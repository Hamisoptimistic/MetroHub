using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.MetroSync.Controls;

/// <summary>
/// A lightweight, read-only selectable text control designed for chat bubbles.
/// Inherits from TextBoxBase to ensure native mouse selection, Ctrl+C copying,
/// and automatic recognition as an interactive control by the MetroHub canvas.
/// Detects URLs, displays them with accent coloring, and underlines them on hover.
/// </summary>
public sealed partial class MetroSyncSelectableTextBlock : RichTextBox
{
    private static readonly char[] PunctuationTrimChars = ['.', ',', ';', '!', '?', '"', '\''];

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MetroSyncSelectableTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTextChanged));

    public static readonly DependencyProperty LinkCommandProperty = DependencyProperty.Register(
        nameof(LinkCommand),
        typeof(ICommand),
        typeof(MetroSyncSelectableTextBlock),
        new PropertyMetadata(null));

    public static readonly DependencyProperty LinkCommandParameterProperty = DependencyProperty.Register(
        nameof(LinkCommandParameter),
        typeof(object),
        typeof(MetroSyncSelectableTextBlock),
        new PropertyMetadata(null));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? LinkCommand
    {
        get => (ICommand?)GetValue(LinkCommandProperty);
        set => SetValue(LinkCommandProperty, value);
    }

    public object? LinkCommandParameter
    {
        get => GetValue(LinkCommandParameterProperty);
        set => SetValue(LinkCommandParameterProperty, value);
    }

    public bool IsSingleLine { get; private set; } = true;
    public double LastLineWidth { get; private set; }

    protected override Size MeasureOverride(Size constraint)
    {
        if (string.IsNullOrEmpty(Text))
        {
            IsSingleLine = true;
            LastLineWidth = 0;
            return new Size(0, 0);
        }

        double maxTextWidth = double.IsInfinity(constraint.Width) || constraint.Width <= 0
            ? 340.0
            : constraint.Width;

        // RichTextBox has an internal content margin and caret padding (~12px)
        double textConstraintWidth = Math.Max(0, maxTextWidth - 12.0);

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        double dpi = 1.0;
        try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }

        var formattedText = new FormattedText(
            Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            Foreground ?? Brushes.White,
            dpi)
        {
            MaxTextWidth = textConstraintWidth
        };

        double lineHeight = formattedText.LineHeight > 0
            ? formattedText.LineHeight
            : FontSize * 1.35;

        IsSingleLine = formattedText.Height <= lineHeight * 1.5;

        if (IsSingleLine)
        {
            LastLineWidth = formattedText.Width + 12.0;
        }
        else
        {
            LastLineWidth = CalculateLastLineWidth(Text, textConstraintWidth, typeface, FontSize, dpi, formattedText.Height) + 12.0;
        }

        double naturalWidth = Math.Min(maxTextWidth, Math.Ceiling(formattedText.Width) + 12.0);

        if (Document != null)
        {
            Document.PageWidth = naturalWidth;
        }

        base.MeasureOverride(new Size(naturalWidth, constraint.Height));

        double finalWidth = naturalWidth;
        double finalHeight = Math.Max(base.DesiredSize.Height, Math.Ceiling(formattedText.Height));

        return new Size(finalWidth, finalHeight);
    }

    private static double CalculateLastLineWidth(
        string text,
        double maxTextWidth,
        Typeface typeface,
        double fontSize,
        double dpi,
        double totalHeight)
    {
        int guessIndex = text.Length - 1;
        while (guessIndex > 0)
        {
            int prevSpace = text.LastIndexOfAny([' ', '\n', '\r', '\t'], guessIndex - 1);
            int testIndex = prevSpace > 0 ? prevSpace : 0;

            var sub = new FormattedText(
                text[..testIndex],
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.White,
                dpi)
            {
                MaxTextWidth = maxTextWidth
            };

            if (sub.Height < totalHeight)
            {
                string lastLine = text[testIndex..].TrimStart();
                var lastLineFt = new FormattedText(
                    lastLine,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.White,
                    dpi)
                {
                    MaxTextWidth = maxTextWidth
                };
                return lastLineFt.Width;
            }

            if (testIndex == 0) break;
            guessIndex = testIndex;
        }

        return maxTextWidth;
    }

    public MetroSyncSelectableTextBlock()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        Focusable = true;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Margin = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        Tag = "InteractiveControl";
        IsUndoEnabled = false;
        ContextMenu = null; // Allows right-click to bubble to the enclosing chat bubble's ContextMenu

        Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Background = Brushes.Transparent
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RebuildDocument();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MetroSyncSelectableTextBlock control)
        {
            control.RebuildDocument();
        }
    }

    private void RebuildDocument()
    {
        var rawText = Text;
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Background = Brushes.Transparent
        };

        if (string.IsNullOrEmpty(rawText))
        {
            Document = doc;
            return;
        }

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = double.NaN
        };

        var matches = UrlRegex().Matches(rawText);
        if (matches.Count == 0)
        {
            paragraph.Inlines.Add(new Run(rawText));
            doc.Blocks.Add(paragraph);
            Document = doc;
            return;
        }

        int lastIndex = 0;
        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                var prefix = rawText[lastIndex..match.Index];
                paragraph.Inlines.Add(new Run(prefix));
            }

            var rawCandidate = match.Value;
            var cleanUrl = CleanUrl(rawCandidate);
            var trailingJunk = rawCandidate[cleanUrl.Length..];

            var linkRun = new Run(cleanUrl);
            var hyperlink = new Hyperlink(linkRun)
            {
                Cursor = Cursors.Hand,
                ToolTip = cleanUrl
            };

            if (Uri.TryCreate(cleanUrl, UriKind.Absolute, out var uri))
            {
                hyperlink.NavigateUri = uri;
            }

            var linkStyle = new Style(typeof(Hyperlink));
            linkStyle.Setters.Add(new Setter(Hyperlink.TextDecorationsProperty, null));
            var linkBrush = TryFindResource("SystemAccentColorPrimaryBrush") as Brush
                            ?? TryFindResource("TextPrimaryBrush") as Brush;
            if (linkBrush != null)
            {
                linkStyle.Setters.Add(new Setter(Hyperlink.ForegroundProperty, linkBrush));
            }

            var hoverTrigger = new Trigger { Property = Hyperlink.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Hyperlink.TextDecorationsProperty, TextDecorations.Underline));
            linkStyle.Triggers.Add(hoverTrigger);
            hyperlink.Style = linkStyle;

            hyperlink.Click += OnHyperlinkClick;
            paragraph.Inlines.Add(hyperlink);

            if (!string.IsNullOrEmpty(trailingJunk))
            {
                paragraph.Inlines.Add(new Run(trailingJunk));
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < rawText.Length)
        {
            paragraph.Inlines.Add(new Run(rawText[lastIndex..]));
        }

        doc.Blocks.Add(paragraph);
        Document = doc;
    }

    private void OnHyperlinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink hyperlink)
        {
            e.Handled = true;
            var url = hyperlink.ToolTip as string ?? hyperlink.NavigateUri?.AbsoluteUri;
            if (string.IsNullOrWhiteSpace(url)) return;

            if (LinkCommand != null && LinkCommand.CanExecute(LinkCommandParameter ?? url))
            {
                LinkCommand.Execute(LinkCommandParameter ?? url);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Ignored
            }
        }
    }

    private static string CleanUrl(string candidate)
    {
        while (candidate.Length > 0)
        {
            char lastChar = candidate[^1];
            if (Array.IndexOf(PunctuationTrimChars, lastChar) >= 0 || lastChar == '>' || lastChar == '<')
            {
                candidate = candidate[..^1];
                continue;
            }

            if (lastChar == ')' && CountOccurrences(candidate, ')') > CountOccurrences(candidate, '('))
            {
                candidate = candidate[..^1];
                continue;
            }

            if (lastChar == ']' && CountOccurrences(candidate, ']') > CountOccurrences(candidate, '['))
            {
                candidate = candidate[..^1];
                continue;
            }

            break;
        }

        return candidate;
    }

    private static int CountOccurrences(string text, char c)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == c) count++;
        }
        return count;
    }
}
