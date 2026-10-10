using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Dialogs;

public partial class NoteViewerDialog : MetroDialog
{
    private readonly TileModel? _tile;
    private readonly string? _filePath;
    private readonly DispatcherTimer _copyTimer;

    public NoteViewerDialog(TileModel tile)
    {
        InitializeComponent();
        _tile = tile;
        _filePath = tile.TargetPath;

        Title = !string.IsNullOrWhiteSpace(tile.Title) ? tile.Title : "Note";
        Subtitle = "Pinned Note";

        if (!string.IsNullOrWhiteSpace(tile.Arguments) &&
            (tile.Arguments.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             tile.Arguments.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            SourceLinkPanel.Visibility = Visibility.Visible;
            SourceUrlText.Text = $"Source: {tile.Arguments}";
        }

        if (!string.IsNullOrWhiteSpace(_filePath) && File.Exists(_filePath))
        {
            try
            {
                NoteContentBox.Text = File.ReadAllText(_filePath);
            }
            catch (Exception ex)
            {
                NoteContentBox.Text = $"Error reading note: {ex.Message}";
            }
        }
        else
        {
            NoteContentBox.Text = "Note content unavailable.";
        }

        _copyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _copyTimer.Tick += (s, e) =>
        {
            _copyTimer.Stop();
            CopyButtonText.Text = "Copy Text";
        };
    }

    public static void Show(Window? owner, TileModel tile)
    {
        var dlg = new NoteViewerDialog(tile);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        dlg.ShowDialog();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(NoteContentBox.Text ?? string.Empty);
            CopyButtonText.Text = "Copied!";
            _copyTimer.Stop();
            _copyTimer.Start();
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "NoteViewerDialog: Failed to copy to clipboard");
        }
    }

    private void OnOpenWebpageClick(object sender, RoutedEventArgs e)
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

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_filePath))
        {
            try
            {
                File.WriteAllText(_filePath, NoteContentBox.Text ?? string.Empty);
            }
            catch (Exception ex)
            {
                Safe.Log(ex, "NoteViewerDialog: Failed to save updated note");
            }
        }
        DialogResult = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
