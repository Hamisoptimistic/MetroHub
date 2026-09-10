using System.Collections.ObjectModel;
using System.IO;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

public static class AppScannerService
{
    public static ObservableCollection<TileModel> GenerateStarterTemplate()
    {
        var tiles = new ObservableCollection<TileModel>();

        // File Explorer (Medium 2x2)
        tiles.Add(CreateAppTile("File Explorer", "explorer.exe", IconExtractorService.ExtractAndCacheIcon("explorer.exe"), 2, 2, "#0078D7", 0, 0));

        // Default Browser (Edge / Chrome)
        string browserPath = FindDefaultBrowser();
        tiles.Add(CreateAppTile("Browser", browserPath, IconExtractorService.ExtractAndCacheIcon(browserPath), 2, 2, "#0080FF", 2, 0));

        // Terminal / PowerShell
        string terminalPath = FindTerminal();
        tiles.Add(CreateAppTile("Terminal", terminalPath, IconExtractorService.ExtractAndCacheIcon(terminalPath), 2, 2, "#4E5664", 4, 0));

        // Task Manager
        string taskmgr = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskmgr.exe");
        tiles.Add(CreateAppTile("Task Manager", taskmgr, IconExtractorService.ExtractAndCacheIcon(taskmgr), 2, 2, "#0063B1", 6, 0));

        // Windows Settings (ms-settings:)
        tiles.Add(new TileModel
        {
            Title = "Settings",
            TargetPath = "ms-settings:",
            TileType = TileType.App,
            SpanX = 2,
            SpanY = 2,
            AccentColor = "#69797E",
            IconPath = IconExtractorService.ExtractAndCacheIcon(taskmgr),
            Col = 8,
            Row = 0,
            X = GridPlacementService.PixelXFromCol(8),
            Y = GridPlacementService.PixelYFromRow(0)
        });

        // Notepad (1x1)
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        tiles.Add(CreateAppTile("Notepad", notepad, IconExtractorService.ExtractAndCacheIcon(notepad), 1, 1, "#107C41", 10, 0));

        // Calculator (1x1)
        string calc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "calc.exe");
        tiles.Add(CreateAppTile("Calculator", calc, IconExtractorService.ExtractAndCacheIcon(calc), 1, 1, "#008272", 10, 1));

        // Command Prompt (1x1)
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        tiles.Add(CreateAppTile("Command Prompt", cmd, IconExtractorService.ExtractAndCacheIcon(cmd), 1, 1, "#303030", 11, 0));

        return tiles;
    }

    private static TileModel CreateAppTile(string title, string path, string? iconPath, int spanX, int spanY, string? color, int col, int row)
    {
        return new TileModel
        {
            Title = title,
            TargetPath = path,
            IconPath = iconPath,
            TileType = TileType.App,
            SpanX = spanX,
            SpanY = spanY,
            AccentColor = color,
            Col = col,
            Row = row,
            X = GridPlacementService.PixelXFromCol(col),
            Y = GridPlacementService.PixelYFromRow(row)
        };
    }

    private static string FindDefaultBrowser()
    {
        string edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        if (File.Exists(edge)) return edge;

        string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        if (File.Exists(chrome)) return chrome;

        return "explorer.exe";
    }

    private static string FindTerminal()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string wt = Path.Combine(localAppData, @"Microsoft\WindowsApps\wt.exe");
        if (File.Exists(wt)) return wt;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
    }
}
