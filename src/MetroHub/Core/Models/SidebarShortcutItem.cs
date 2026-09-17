using System;
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
    private int _sortOrder = 0;

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
            }
        }
    }

    public string IconSymbol
    {
        get => _iconSymbol;
        set => SetField(ref _iconSymbol, value);
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
    public static string DetectSmartIcon(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
            return "Folder24";

        string name = Path.GetFileName(nameOrPath.TrimEnd('\\', '/')).ToLowerInvariant();

        // Games / Gaming / Steam / Emulators
        if (name.Contains("game") || name.Contains("steam") || name.Contains("epic") || name.Contains("rom") || name.Contains("play"))
            return "Games24";

        // Code / Development / Git / Projects / Source
        if (name.Contains("code") || name.Contains("dev") || name.Contains("repo") || name.Contains("git") || 
            name.Contains("src") || name.Contains("source") || name.Contains("proj") || name.Contains("hub") ||
            name.Contains("build") || name.Contains("script") || name.Contains("worksp"))
            return "Code24";

        // Screenshots / Captures
        if (name.Contains("screen") || name.Contains("capture") || name.Contains("shot"))
            return "Camera24";

        // Recordings / OBS / Streams
        if (name.Contains("record") || name.Contains("obs") || name.Contains("stream") || name.Contains("vod"))
            return "Video24";

        // Vault / Private / Secret / Passwords / Keys
        if (name.Contains("vault") || name.Contains("secret") || name.Contains("priv") || name.Contains("key") || name.Contains("pass"))
            return "LockClosed24";

        // Study / School / College / Books / Reading
        if (name.Contains("study") || name.Contains("school") || name.Contains("college") || name.Contains("class") || name.Contains("read") || name.Contains("course"))
            return "BookOpen24";

        // Finance / Money / Bank / Crypto / Wallet / Taxes
        if (name.Contains("money") || name.Contains("bank") || name.Contains("crypto") || name.Contains("wallet") || name.Contains("bill") || name.Contains("tax") || name.Contains("finance"))
            return "Money24";

        // 3D / Blender / Render / CAD
        if (name.Contains("3d") || name.Contains("blender") || name.Contains("render") || name.Contains("cad") || name.Contains("model"))
            return "Cube24";

        // Temp / Cache / Logs / Dump
        if (name.Contains("temp") || name.Contains("cache") || name.Contains("log") || name.Contains("dump"))
            return "History24";

        // Favorites / Bookmarks / Starred
        if (name.Contains("fav") || name.Contains("star") || name.Contains("bookmark"))
            return "Star24";

        // Fonts
        if (name.Contains("font"))
            return "TextFont24";

        // Downloads
        if (name.Contains("download"))
            return "ArrowDownload24";

        // Documents / Notes / PDFs / Text
        if (name.Contains("doc") || name.Contains("note") || name.Contains("book") || name.Contains("pdf") || name.Contains("text"))
            return "Document24";

        // Photos / Images / Wallpapers / Art
        if (name.Contains("photo") || name.Contains("pic") || name.Contains("image") || name.Contains("wall") || name.Contains("camera") || name.Contains("art"))
            return "Image24";

        // Music / Audio / Songs / Podcasts / Beats
        if (name.Contains("music") || name.Contains("song") || name.Contains("audio") || name.Contains("sound") || name.Contains("track") || name.Contains("podcast") || name.Contains("beat"))
            return "MusicNote224";

        // Videos / Movies / Clips / TV / Film
        if (name.Contains("video") || name.Contains("movie") || name.Contains("film") || name.Contains("clip") || name.Contains("tv"))
            return "Video24";

        // Work / Office / Business / Clients
        if (name.Contains("work") || name.Contains("office") || name.Contains("job") || name.Contains("biz") || name.Contains("client"))
            return "Briefcase24";

        // Cloud / Drive / Sync / OneDrive / Dropbox
        if (name.Contains("cloud") || name.Contains("drive") || name.Contains("onedrive") || name.Contains("dropbox") || name.Contains("sync") || name.Contains("box"))
            return "Cloud24";

        // Desktop
        if (name.Contains("desktop"))
            return "Desktop24";

        // Root Drives (C:\, D:\, etc.)
        if (nameOrPath.EndsWith(":\\") || nameOrPath.EndsWith(":") || name.Contains("drive") || name.Contains("disk"))
            return "HardDrive24";

        // Archives / Zip / Backups / ISO
        if (name.Contains("zip") || name.Contains("rar") || name.Contains("7z") || name.Contains("tar") || name.Contains("archive") || name.Contains("backup") || name.Contains("iso"))
            return "FolderZip24";

        // Tools / Utilities / Software / Installers
        if (name.Contains("tool") || name.Contains("util") || name.Contains("soft") || name.Contains("app") || name.Contains("install"))
            return "Wrench24";

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
