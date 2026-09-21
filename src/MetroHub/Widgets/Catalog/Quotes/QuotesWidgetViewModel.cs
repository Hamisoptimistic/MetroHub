using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Catalog.Quotes;

/// <summary>
/// ViewModel for the 8x3 Quotes Widget.
/// Loads quotes from embedded resources or %LOCALAPPDATA%\MetroHub\quotes.json.
/// Displays a deterministic daily quote with manual shuffle and clipboard copy features.
/// Supports Google Fonts (Merriweather, Quintessential) and system typefaces with real-time font and style switching.
/// </summary>
public partial class QuotesWidgetViewModel : WidgetViewModelBase
{
    private readonly List<QuoteModel> _quotes = new();
    private int _currentQuoteIndex = -1;
    private DispatcherTimer? _copiedFeedbackTimer;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Banner3 // Strictly 8x3
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(DisplayAuthorName))]
    [NotifyPropertyChangedFor(nameof(DisplayAuthor))]
    private QuoteModel? _currentQuote;

    [ObservableProperty]
    private string _revealedText = string.Empty;

    [ObservableProperty]
    private double _cursorOpacity = 0.0;

    [ObservableProperty]
    private bool _isAuthorVisible = true;

    private CancellationTokenSource? _typingCts;

    [ObservableProperty]
    private QuoteFontFamilyChoice _selectedFont = QuoteFontFamilyChoice.Merriweather;

    [ObservableProperty]
    private QuoteFontStyleChoice _selectedStyle = QuoteFontStyleChoice.Italic;

    [ObservableProperty]
    private string _quoteFontFamily = "pack://application:,,,/MetroHub;component/Assets/Fonts/#Merriweather, Georgia, serif";

    [ObservableProperty]
    private FontStyle _quoteFontStyle = FontStyles.Italic;

    [ObservableProperty]
    private FontWeight _quoteFontWeight = FontWeights.Normal;

    [ObservableProperty]
    private SymbolRegular _copyIconSymbol = SymbolRegular.Copy24;

    [ObservableProperty]
    private string _copyToolTip = "Copy Quote";

    public string DisplayText => CurrentQuote != null && !string.IsNullOrWhiteSpace(CurrentQuote.Text)
        ? CurrentQuote.Text
        : "To find yourself, think for yourself.";

    public string DisplayAuthorName => CurrentQuote != null && !string.IsNullOrWhiteSpace(CurrentQuote.Author)
        ? CurrentQuote.Author
        : "Socrates";

    public string DisplayAuthor => $"— {DisplayAuthorName}";

    public QuotesWidgetViewModel(TileModel model) : base(model)
    {
        // Enforce 8x3 banner dimensions
        if (model.SpanX != 8 || model.SpanY != 3)
        {
            model.SpanX = 8;
            model.SpanY = 3;
        }

        LoadQuotes();
        InitializeQuoteSelection(model.SettingsJson);
        UpdateFontProperties();
    }

    private void LoadQuotes()
    {
        _quotes.Clear();

        // 1. Try loading user override from %LOCALAPPDATA%\MetroHub\quotes.json
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userQuotesFile = Path.Combine(localAppData, "MetroHub", "quotes.json");

            if (File.Exists(userQuotesFile))
            {
                string json = File.ReadAllText(userQuotesFile, Encoding.UTF8);
                var userList = JsonSerializer.Deserialize<List<QuoteModel>>(json);
                if (userList != null && userList.Count > 0)
                {
                    _quotes.AddRange(userList);
                }
            }
        }
        catch
        {
            // Ignore error and fall back to embedded resource
        }

        // 2. If no user override or file was empty, load from embedded resource
        if (_quotes.Count == 0)
        {
            try
            {
                var resourceUri = new Uri("pack://application:,,,/MetroHub;component/Assets/quotes.json", UriKind.Absolute);
                var streamInfo = Application.GetResourceStream(resourceUri);
                if (streamInfo != null)
                {
                    using var reader = new StreamReader(streamInfo.Stream, Encoding.UTF8);
                    string json = reader.ReadToEnd();
                    var list = JsonSerializer.Deserialize<List<QuoteModel>>(json);
                    if (list != null && list.Count > 0)
                    {
                        _quotes.AddRange(list);
                    }
                }
            }
            catch
            {
                // Fallback will supply defaults
            }
        }

        // 3. Absolute fallback in case of missing asset
        if (_quotes.Count == 0)
        {
            _quotes.Add(new QuoteModel
            {
                Author = "Socrates",
                Text = "The unexamined life is not worth living."
            });
            _quotes.Add(new QuoteModel
            {
                Author = "Marcus Aurelius",
                Text = "You have power over your mind - not outside events. Realize this, and you will find strength."
            });
        }
    }

    private void InitializeQuoteSelection(string? settingsJson)
    {
        QuotesWidgetSettings? settings = null;
        if (!string.IsNullOrWhiteSpace(settingsJson))
        {
            try
            {
                settings = JsonSerializer.Deserialize<QuotesWidgetSettings>(settingsJson);
            }
            catch
            {
                // Fallback to default
            }
        }

        if (settings != null)
        {
            SelectedFont = settings.Font;
            SelectedStyle = settings.Style;
        }

        var today = DateTime.Today;

        if (settings != null &&
            settings.LastYear == today.Year &&
            settings.LastDayOfYear == today.DayOfYear &&
            settings.CurrentQuoteIndex >= 0 &&
            settings.CurrentQuoteIndex < _quotes.Count)
        {
            _currentQuoteIndex = settings.CurrentQuoteIndex;
        }
        else
        {
            // Pick deterministic daily quote based on date hash
            _currentQuoteIndex = GetDailyQuoteIndex(today);
            SaveSettings();
        }

        UpdateCurrentQuote();
    }

    private int GetDailyQuoteIndex(DateTime date)
    {
        if (_quotes.Count <= 1) return 0;

        int days = (int)(date - new DateTime(2025, 1, 1)).TotalDays;
        uint hash = (uint)days;
        hash = ((hash >> 16) ^ hash) * 0x45d9f3b;
        hash = ((hash >> 16) ^ hash) * 0x45d9f3b;
        hash = (hash >> 16) ^ hash;
        return (int)(hash % (uint)_quotes.Count);
    }

    private void UpdateCurrentQuote(bool animate = false)
    {
        if (_quotes.Count == 0) return;
        if (_currentQuoteIndex < 0 || _currentQuoteIndex >= _quotes.Count)
        {
            _currentQuoteIndex = 0;
        }
        CurrentQuote = _quotes[_currentQuoteIndex];

        _typingCts?.Cancel();
        _typingCts?.Dispose();
        _typingCts = null;

        if (!animate)
        {
            RevealedText = DisplayText;
            CursorOpacity = 0.0;
            IsAuthorVisible = true;
        }
        else
        {
            RevealedText = string.Empty;
            CursorOpacity = 1.0;
            IsAuthorVisible = false;
            _typingCts = new CancellationTokenSource();
            var token = _typingCts.Token;
            _ = RevealQuoteTypewriterAsync(DisplayText, token);
        }
    }

    private async Task RevealQuoteTypewriterAsync(string fullQuote, CancellationToken token)
    {
        if (string.IsNullOrEmpty(fullQuote))
        {
            RevealedText = string.Empty;
            CursorOpacity = 0.0;
            IsAuthorVisible = true;
            return;
        }

        CursorOpacity = 1.0;
        var builder = new StringBuilder(fullQuote.Length);

        for (int i = 0; i < fullQuote.Length; i++)
        {
            if (token.IsCancellationRequested) return;

            char c = fullQuote[i];
            builder.Append(c);
            RevealedText = builder.ToString();

            // Smooth typing cadence: 50-60 Hz for characters, natural organic micro-pauses for punctuation
            int delayMs = c switch
            {
                '.' or '!' or '?' => 130,
                ',' or ';' or ':' => 75,
                '—' or '-' => 50,
                ' ' => 22,
                _ => 16
            };

            try
            {
                await Task.Delay(delayMs, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (token.IsCancellationRequested) return;

        RevealedText = fullQuote;

        // Hold cleanly on the finished quote for 250ms with zero jumping or layout shift, then dismiss cursor
        try
        {
            await Task.Delay(250, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
        {
            CursorOpacity = 0.0;
            IsAuthorVisible = true;
        }
    }

    public void SetFont(QuoteFontFamilyChoice font)
    {
        SelectedFont = font;
        UpdateFontProperties();
        SaveSettings();
    }

    public void SetStyle(QuoteFontStyleChoice style)
    {
        SelectedStyle = style;
        UpdateFontProperties();
        SaveSettings();
    }

    private void UpdateFontProperties()
    {
        QuoteFontFamily = SelectedFont switch
        {
            QuoteFontFamilyChoice.Merriweather => "pack://application:,,,/MetroHub;component/Assets/Fonts/#Merriweather, Georgia, serif",
            QuoteFontFamilyChoice.Quintessential => "pack://application:,,,/MetroHub;component/Assets/Fonts/#Quintessential, Georgia, serif",
            QuoteFontFamilyChoice.Georgia => "Georgia, 'Times New Roman', serif",
            QuoteFontFamilyChoice.Palatino => "'Palatino Linotype', Georgia, serif",
            QuoteFontFamilyChoice.SegoeUI => "Segoe UI Variable Text, Segoe UI, sans-serif",
            _ => "pack://application:,,,/MetroHub;component/Assets/Fonts/#Merriweather, Georgia, serif"
        };

        QuoteFontStyle = SelectedStyle switch
        {
            QuoteFontStyleChoice.Regular => FontStyles.Normal,
            _ => FontStyles.Italic
        };

        QuoteFontWeight = FontWeights.Normal;
    }

    [RelayCommand]
    public void NextQuote()
    {
        if (_quotes.Count <= 1) return;

        int nextIdx;
        do
        {
            nextIdx = Random.Shared.Next(_quotes.Count);
        } while (nextIdx == _currentQuoteIndex && _quotes.Count > 1);

        _currentQuoteIndex = nextIdx;
        UpdateCurrentQuote(animate: true);
        SaveSettings();
    }

    [RelayCommand]
    public void CopyQuote()
    {
        if (CurrentQuote == null) return;

        string textToCopy = $"“{DisplayText}” — {DisplayAuthorName}";

        try
        {
            if (Application.Current?.Dispatcher?.CheckAccess() == true)
            {
                Clipboard.SetText(textToCopy);
            }
            else
            {
                Application.Current?.Dispatcher?.Invoke(() => Clipboard.SetText(textToCopy));
            }

            TriggerCopyFeedback();
        }
        catch
        {
            // In case clipboard is locked by external process
        }
    }

    private void TriggerCopyFeedback()
    {
        CopyIconSymbol = SymbolRegular.Checkmark24;
        CopyToolTip = "Copied!";

        _copiedFeedbackTimer?.Stop();
        _copiedFeedbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _copiedFeedbackTimer.Tick += (s, e) =>
        {
            _copiedFeedbackTimer.Stop();
            CopyIconSymbol = SymbolRegular.Copy24;
            CopyToolTip = "Copy Quote";
        };
        _copiedFeedbackTimer.Start();
    }

    public override void SaveSettings()
    {
        var settings = new QuotesWidgetSettings
        {
            LastYear = DateTime.Today.Year,
            LastDayOfYear = DateTime.Today.DayOfYear,
            CurrentQuoteIndex = _currentQuoteIndex,
            Font = SelectedFont,
            Style = SelectedStyle
        };

        Model.SettingsJson = JsonSerializer.Serialize(settings);
        MainWindow.Current?.SaveGroupsAndLayout();
    }

    public override void Pause()
    {
        base.Pause();
        if (_typingCts != null)
        {
            _typingCts.Cancel();
            _typingCts.Dispose();
            _typingCts = null;
            RevealedText = DisplayText;
            CursorOpacity = 0.0;
            IsAuthorVisible = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _typingCts?.Cancel();
            _typingCts?.Dispose();
            _typingCts = null;
            _copiedFeedbackTimer?.Stop();
            _copiedFeedbackTimer = null;
        }
        base.Dispose(disposing);
    }
}
