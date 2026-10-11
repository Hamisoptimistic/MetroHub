using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Views;

namespace MetroHub.Presentation.Dialogs;

public partial class NoteViewerDialog : MetroDialog
{
    private readonly TileModel? _tile;
    private readonly string? _filePath;
    private readonly DispatcherTimer _copyTimer;

    protected override IInputElement? InitialFocusedElement => CopyButton;

    public NoteViewerDialog(TileModel tile)
    {
        InitializeComponent();
        _tile = tile;
        _filePath = tile.TargetPath;

        Title = !string.IsNullOrWhiteSpace(tile.Title) ? tile.Title : "Note";
        TitleTextBlock.Text = Title;

        // Configure URL / Web Extension state
        ConfigureSourceUrl(tile.Arguments);

        // Load note text from disk
        LoadNoteContent();

        // Clipboard copy feedback timer
        _copyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _copyTimer.Tick += (s, e) =>
        {
            _copyTimer.Stop();
            CopyButtonText.Text = "Copy";
            CopyButtonIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Copy24;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // Apply native Windows 11 DWM Acrylic composition with sharp Metro 2px border
            NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TRANSIENTWINDOW, NativeMethods.DWMWCP_DONOTROUND);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _copyTimer.Stop();
        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            OnCopyClick(CopyButton, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    public static void Show(Window? owner, TileModel tile)
    {
        using (MainWindow.EnterDialogScope())
        {
            var dlg = new NoteViewerDialog(tile)
            {
                Owner = owner ?? Application.Current?.MainWindow
            };
            dlg.ShowDialog();
        }
    }

    private void ConfigureSourceUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) ||
            (!rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
             !rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            SourceWebButton.Visibility = Visibility.Collapsed;
            return;
        }

        string normalizedUrl = WebFaviconService.NormalizeUrl(rawUrl);
        string siteName = ExtractWebsiteName(normalizedUrl);

        SourceWebButton.ToolTip = normalizedUrl;
        SourceWebButtonText.Text = siteName;
        SourceWebButton.Visibility = Visibility.Visible;

        // Asynchronously load favicon if cached/available
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                string? iconPath = await WebFaviconService.GetFaviconPathAsync(normalizedUrl);
                if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    FaviconImage.Source = bitmap;
                    FaviconImage.Visibility = Visibility.Visible;
                    FallbackWebIcon.Visibility = Visibility.Collapsed;
                }
                else
                {
                    FaviconImage.Visibility = Visibility.Collapsed;
                    FallbackWebIcon.Visibility = Visibility.Visible;
                }
            }
            catch
            {
                FaviconImage.Visibility = Visibility.Collapsed;
                FallbackWebIcon.Visibility = Visibility.Visible;
            }
        });
    }

    private static string ExtractWebsiteName(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Website";

        string inferred = WebFaviconService.InferTitleFromUrl(url);
        if (!string.IsNullOrWhiteSpace(inferred) &&
            !inferred.Equals("Web Link", StringComparison.OrdinalIgnoreCase) &&
            !inferred.Contains('.'))
        {
            return inferred;
        }

        string domain = WebFaviconService.ExtractDomain(url);
        if (string.IsNullOrWhiteSpace(domain)) return "Website";

        var parts = domain.Split('.');
        if (parts.Length >= 2)
        {
            string brand = parts[^2];
            if ((brand.Equals("co", StringComparison.OrdinalIgnoreCase) ||
                 brand.Equals("com", StringComparison.OrdinalIgnoreCase) ||
                 brand.Equals("org", StringComparison.OrdinalIgnoreCase) ||
                 brand.Equals("net", StringComparison.OrdinalIgnoreCase) ||
                 brand.Equals("gov", StringComparison.OrdinalIgnoreCase) ||
                 brand.Equals("edu", StringComparison.OrdinalIgnoreCase)) &&
                parts.Length >= 3)
            {
                brand = parts[^3];
            }

            if (!string.IsNullOrWhiteSpace(brand))
            {
                return char.ToUpperInvariant(brand[0]) + brand[1..].ToLowerInvariant();
            }
        }

        return "Website";
    }

    private void LoadNoteContent()
    {
        if (!string.IsNullOrWhiteSpace(_filePath) && File.Exists(_filePath))
        {
            try
            {
                string text = File.ReadAllText(_filePath);
                NoteContentBox.Text = text;
                UpdateReadingMetadata(text);
            }
            catch (Exception ex)
            {
                NoteContentBox.Text = $"Error reading note: {ex.Message}";
                UpdateReadingMetadata(string.Empty);
            }
        }
        else
        {
            NoteContentBox.Text = "Note content unavailable.";
            UpdateReadingMetadata(string.Empty);
        }
    }

    private void UpdateReadingMetadata(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ReadingMetadataText.Text = "0 words · 0 chars";
            return;
        }

        int charCount = text.Length;
        int wordCount = text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

        string wordPart = wordCount == 1 ? "1 word" : $"{wordCount:N0} words";
        string charPart = charCount == 1 ? "1 char" : $"{charCount:N0} chars";
        ReadingMetadataText.Text = $"{wordPart} · {charPart}";
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnSourceWebButtonClick(object sender, RoutedEventArgs e)
    {
        if (_tile != null && !string.IsNullOrWhiteSpace(_tile.Arguments))
        {
            try
            {
                ProcessLauncherService.LaunchTargetAsync(_tile.Arguments, null, false, _tile.Title);
            }
            catch (Exception ex)
            {
                Safe.Log(ex, "NoteViewerDialog: Failed to open source URL");
            }
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(NoteContentBox.Text ?? string.Empty);
            CopyButtonText.Text = "Copied!";
            CopyButtonIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Checkmark24;
            _copyTimer.Stop();
            _copyTimer.Start();
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "NoteViewerDialog: Failed to copy to clipboard");
        }
    }

    private void OnOpenWithClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _filePath,
                Verb = "openas",
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception win32Ex) when (win32Ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED: User clicked "Cancel" in Open With picker.
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "NoteViewerDialog: 'openas' failed, falling back to default launch");
            ProcessLauncherService.LaunchTargetAsync(_filePath, null, false, _tile?.Title);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DismissDialog();
    }
}
