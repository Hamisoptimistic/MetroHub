using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace MetroHub.Core.Models;

public enum SidebarShortcutType
{
    SystemFolder,
    CustomFolder,
    Application,
    WebUrl,
    Command,
    Separator
}

/// <summary>
/// Represents a user-customizable shortcut pinned to the left navigation rail.
/// Implements INotifyPropertyChanged for real-time icon and title updates.
/// </summary>
public sealed class SidebarShortcutItem : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _title = string.Empty;
    private string _target = string.Empty;
    private SidebarShortcutType _targetType = SidebarShortcutType.SystemFolder;
    private string _iconSymbol = "Folder24";
    private string? _customIconPath;
    private bool _isRemovable = true;
    private int _sortOrder;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Target
    {
        get => _target;
        set => SetField(ref _target, value);
    }

    public SidebarShortcutType TargetType
    {
        get => _targetType;
        set
        {
            if (SetField(ref _targetType, value))
            {
                OnPropertyChanged(nameof(IsSeparator));
                OnPropertyChanged(nameof(IsFolder));
                OnPropertyChanged(nameof(IsApplication));
                OnPropertyChanged(nameof(IsWebUrl));
                OnPropertyChanged(nameof(IsNotWebUrl));
            }
        }
    }

    public string IconSymbol
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_iconSymbol) || _iconSymbol.Equals("HardDrive24", StringComparison.OrdinalIgnoreCase))
            {
                return "Storage24";
            }
            return _iconSymbol;
        }
        set
        {
            string val = (!string.IsNullOrWhiteSpace(value) && value.Equals("HardDrive24", StringComparison.OrdinalIgnoreCase)) ? "Storage24" : value;
            SetField(ref _iconSymbol, string.IsNullOrWhiteSpace(val) ? "Folder24" : val);
        }
    }

    public string? CustomIconPath
    {
        get => _customIconPath;
        set
        {
            if (SetField(ref _customIconPath, value))
            {
                OnPropertyChanged(nameof(HasCustomIcon));
            }
        }
    }

    public bool HasCustomIcon => !string.IsNullOrWhiteSpace(_customIconPath);
    public bool IsSeparator => TargetType == SidebarShortcutType.Separator;
    public bool IsFolder => TargetType == SidebarShortcutType.SystemFolder || TargetType == SidebarShortcutType.CustomFolder;
    public bool IsApplication => TargetType == SidebarShortcutType.Application;
    public bool IsWebUrl => TargetType == SidebarShortcutType.WebUrl;
    public bool IsNotWebUrl => TargetType != SidebarShortcutType.WebUrl;

    public bool IsRemovable
    {
        get => _isRemovable;
        set => SetField(ref _isRemovable, value);
    }

    public int SortOrder
    {
        get => _sortOrder;
        set => SetField(ref _sortOrder, value);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Intelligently detects a distinct Fluent vector icon based on folder name/path keywords.
    /// Covers 100% of standard desktop folder categories.
    /// </summary>
    private static readonly SearchValues<string> s_gamesKeywords = SearchValues.Create(["game", "steam", "epic", "rom", "play"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_codeKeywords = SearchValues.Create(["code", "dev", "repo", "git", "src", "source", "proj", "hub", "build", "script", "worksp"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_screenKeywords = SearchValues.Create(["screen", "capture", "shot"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_recordKeywords = SearchValues.Create(["record", "obs", "stream", "vod"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_vaultKeywords = SearchValues.Create(["vault", "secret", "priv", "key", "pass"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_studyKeywords = SearchValues.Create(["study", "school", "college", "class", "read", "course"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_financeKeywords = SearchValues.Create(["money", "bank", "crypto", "wallet", "bill", "tax", "finance"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_cubeKeywords = SearchValues.Create(["3d", "blender", "render", "cad", "model"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_historyKeywords = SearchValues.Create(["temp", "cache", "log", "dump"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_starKeywords = SearchValues.Create(["fav", "star", "bookmark"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_docKeywords = SearchValues.Create(["doc", "note", "book", "pdf", "text"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_imageKeywords = SearchValues.Create(["photo", "pic", "image", "wall", "camera", "art"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_musicKeywords = SearchValues.Create(["music", "song", "audio", "sound", "track", "podcast", "beat"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_videoKeywords = SearchValues.Create(["video", "movie", "film", "clip", "tv"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_workKeywords = SearchValues.Create(["work", "office", "job", "biz", "client"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_cloudKeywords = SearchValues.Create(["cloud", "drive", "onedrive", "dropbox", "sync", "box"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_storageKeywords = SearchValues.Create(["drive", "disk"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_archiveKeywords = SearchValues.Create(["zip", "rar", "7z", "tar", "archive", "backup", "iso"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_toolKeywords = SearchValues.Create(["tool", "util", "soft", "app", "install"], StringComparison.OrdinalIgnoreCase);

    public static string DetectSmartIcon(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
            return "Folder24";

        ReadOnlySpan<char> span = Path.GetFileName(nameOrPath.TrimEnd('\\', '/')).AsSpan();

        if (span.ContainsAny(s_gamesKeywords)) return "Games24";
        if (span.ContainsAny(s_codeKeywords)) return "Code24";
        if (span.ContainsAny(s_screenKeywords)) return "Camera24";
        if (span.ContainsAny(s_recordKeywords)) return "Video24";
        if (span.ContainsAny(s_vaultKeywords)) return "LockClosed24";
        if (span.ContainsAny(s_studyKeywords)) return "BookOpen24";
        if (span.ContainsAny(s_financeKeywords)) return "Money24";
        if (span.ContainsAny(s_cubeKeywords)) return "Cube24";
        if (span.ContainsAny(s_historyKeywords)) return "History24";
        if (span.ContainsAny(s_starKeywords)) return "Star24";
        if (span.Contains("font", StringComparison.OrdinalIgnoreCase)) return "TextFont24";
        if (span.Contains("download", StringComparison.OrdinalIgnoreCase)) return "ArrowDownload24";
        if (span.ContainsAny(s_docKeywords)) return "Document24";
        if (span.ContainsAny(s_imageKeywords)) return "Image24";
        if (span.ContainsAny(s_musicKeywords)) return "MusicNote224";
        if (span.ContainsAny(s_videoKeywords)) return "Video24";
        if (span.ContainsAny(s_workKeywords)) return "Briefcase24";
        if (span.ContainsAny(s_cloudKeywords)) return "Cloud24";
        if (span.Contains("desktop", StringComparison.OrdinalIgnoreCase)) return "Desktop24";
        if (nameOrPath.EndsWith(":\\", StringComparison.Ordinal) || nameOrPath.EndsWith(':') || span.ContainsAny(s_storageKeywords)) return "Storage24";
        if (span.ContainsAny(s_archiveKeywords)) return "FolderZip24";
        if (span.ContainsAny(s_toolKeywords)) return "Wrench24";

        return "Folder24";
    }

    /// <summary>
    /// Creates the lean default shortcuts set for fresh installs.
    /// </summary>
    public static List<SidebarShortcutItem> CreateDefaultList()
    {
        return new List<SidebarShortcutItem>
        {
            new()
            {
                Id = "default_explorer",
                Title = "File Explorer",
                Target = "explorer.exe",
                TargetType = SidebarShortcutType.SystemFolder,
                IconSymbol = "Folder24",
                IsRemovable = true,
                SortOrder = 0
            },
            new()
            {
                Id = "default_settings",
                Title = "Settings",
                Target = "ms-settings:",
                TargetType = SidebarShortcutType.Command,
                IconSymbol = "Settings24",
                IsRemovable = true,
                SortOrder = 1
            }
        };
    }
}
