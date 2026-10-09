using System;
using System.IO;
using System.Linq;

namespace MetroHub.Core.Services;

/// <summary>
/// Centralized directory and file path resolver for MetroHub application state.
/// Organizes application data into clean categories (config, cache, logs, backups)
/// while guaranteeing 100% backwards compatibility for existing user directories.
/// </summary>
public static class AppPaths
{
    private static string? _customAppDataDir;

    private static readonly bool _isTestProcess = DetectTestEnvironment();

    public static bool IsTestProcess => _isTestProcess;

    private static readonly string _defaultTestSandboxDir = Path.Combine(
        Path.GetTempPath(),
        "MetroHub_TestSandbox_Default");

    private static bool DetectTestEnvironment()
    {
        try
        {
            string proc = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            if (proc.IndexOf("testhost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                proc.IndexOf("vstest", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string domainName = AppDomain.CurrentDomain.FriendlyName;
            if (domainName.IndexOf("testhost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                domainName.IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Optional override for isolated unit testing or portable runs.
    /// When null, defaults to %LocalAppData%\MetroHub in production, or %TEMP%\MetroHub_TestSandbox_Default in unit test hosts.
    /// </summary>
    public static string? CustomAppDataDir
    {
        get => _customAppDataDir;
        set => _customAppDataDir = value;
    }

    /// <summary>
    /// Base application directory: %LocalAppData%\MetroHub in production,
    /// or isolated sandboxes in unit test hosts.
    /// </summary>
    public static string AppDataDir => _customAppDataDir ?? (_isTestProcess
        ? _defaultTestSandboxDir
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MetroHub"));

    // ── Categorized subdirectories ───────────────────────────
    public static string ConfigDir => Path.Combine(AppDataDir, "config");
    public static string DataDir => Path.Combine(AppDataDir, "data");
    public static string CacheDir => Path.Combine(AppDataDir, "cache");
    public static string LogsDir => Path.Combine(AppDataDir, "logs");
    public static string BackupsDir => Path.Combine(AppDataDir, "backups");

    // ── Persistent User Assets (Pasted Data) ─────────────────
    public static string PastedImagesDir => Path.Combine(DataDir, "pasted_images");
    public static string PastedNotesDir => Path.Combine(DataDir, "pasted_notes");

    // ── Widget state (per-tile autosave mirrors) ─────────────
    /// <summary>Root for widget-owned state files: config\widgets\{widgetId}\{tileId}.json.</summary>
    public static string WidgetStateDir => Path.Combine(ConfigDir, "widgets");
    /// <summary>Rollover copies for widget state files: backups\widgets\{widgetId}\{tileId}.json.bak.</summary>
    public static string WidgetStateBakDir => Path.Combine(BackupsDir, "widgets");

    // ── Sub-caches ───────────────────────────────────────────
    public static string PrimaryIconsDir => Path.Combine(CacheDir, "icons");
    public static string LegacyIconsDir => Path.Combine(AppDataDir, "icons");

    public static string PrimaryWallpapersDir => Path.Combine(CacheDir, "WallpapersCache");
    public static string LegacyWallpapersDir => Path.Combine(AppDataDir, "WallpapersCache");

    public static string PrimaryRoverSoundsDir => Path.Combine(CacheDir, "RoverSounds");
    public static string LegacyRoverSoundsDir => Path.Combine(AppDataDir, "RoverSounds");

    // ── Path resolution properties ───────────────────────────

    // Config
    public static string LayoutPath => ResolveFilePath(
        Path.Combine(ConfigDir, "layout.json"),
        Path.Combine(AppDataDir, "layout.json"));

    public static string LayoutBakPath => ResolveFilePath(
        Path.Combine(BackupsDir, "layout.json.bak"),
        Path.Combine(AppDataDir, "layout.json.bak"));

    public static string GroupsPath => ResolveFilePath(
        Path.Combine(ConfigDir, "groups.json"),
        Path.Combine(AppDataDir, "groups.json"));

    public static string GroupsBakPath => ResolveFilePath(
        Path.Combine(BackupsDir, "groups.json.bak"),
        Path.Combine(AppDataDir, "groups.json.bak"));

    public static string SettingsPath => ResolveFilePath(
        Path.Combine(ConfigDir, "settings.json"),
        Path.Combine(AppDataDir, "settings.json"));

    public static string SettingsBakPath => ResolveFilePath(
        Path.Combine(BackupsDir, "settings.json.bak"),
        Path.Combine(AppDataDir, "settings.json.bak"));

    public static string QuotesPath => ResolveFilePath(
        Path.Combine(ConfigDir, "quotes.json"),
        Path.Combine(AppDataDir, "quotes.json"));

    // Search Usage History
    public static string SearchHistoryPath => ResolveFilePath(
        Path.Combine(ConfigDir, "search_history.json"),
        Path.Combine(AppDataDir, "search_history.json"));

    public static string SearchHistoryBakPath => ResolveFilePath(
        Path.Combine(BackupsDir, "search_history.json.bak"),
        Path.Combine(AppDataDir, "search_history.json.bak"));

    // Workspaces
    /// <summary>Directory storing per-workspace layouts and metadata: %LocalAppData%\MetroHub\workspaces.</summary>
    public static string WorkspacesDir => Path.Combine(AppDataDir, "workspaces");

    /// <summary>Workspaces manifest tracking order and active workspace: %LocalAppData%\MetroHub\workspaces\workspaces.json.</summary>
    public static string WorkspacesManifestPath => Path.Combine(WorkspacesDir, "workspaces.json");

    /// <summary>Backup path for workspaces manifest: %LocalAppData%\MetroHub\backups\workspaces.json.bak.</summary>
    public static string WorkspacesManifestBakPath => Path.Combine(BackupsDir, "workspaces.json.bak");

    /// <summary>Preservation directory for deleted workspaces: %LocalAppData%\MetroHub\workspaces_trash.</summary>
    public static string WorkspacesTrashDir => Path.Combine(AppDataDir, "workspaces_trash");

    public static string GetWorkspaceDir(string workspaceId) => Path.Combine(WorkspacesDir, workspaceId);
    public static string GetWorkspaceLayoutPath(string workspaceId) => Path.Combine(GetWorkspaceDir(workspaceId), "layout.json");
    public static string GetWorkspaceLayoutBakPath(string workspaceId) => Path.Combine(GetWorkspaceDir(workspaceId), "layout.json.bak");
    public static string GetWorkspaceGroupsPath(string workspaceId) => Path.Combine(GetWorkspaceDir(workspaceId), "groups.json");
    public static string GetWorkspaceGroupsBakPath(string workspaceId) => Path.Combine(GetWorkspaceDir(workspaceId), "groups.json.bak");

    // Cache
    public static string AppsCachePath => ResolveFilePath(
        Path.Combine(CacheDir, "apps_cache.json"),
        Path.Combine(AppDataDir, "apps_cache.json"));

    public static string AppsCacheBakPath => ResolveFilePath(
        Path.Combine(BackupsDir, "apps_cache.json.bak"),
        Path.Combine(AppDataDir, "apps_cache.json.bak"));

    public static string RadioCatalogPath => ResolveFilePath(
        Path.Combine(CacheDir, "radio_catalog.json"),
        Path.Combine(AppDataDir, "radio_catalog.json"));

    public static string RadioCatalogBakPath => ResolveFilePath(
        Path.Combine(BackupsDir, "radio_catalog.json.bak"),
        Path.Combine(AppDataDir, "radio_catalog.json.bak"));

    public static string WeatherCachePath => ResolveFilePath(
        Path.Combine(CacheDir, "v1_weather_cache.json"),
        Path.Combine(AppDataDir, "v1_weather_cache.json"));

    public static string LocationCachePath => ResolveFilePath(
        Path.Combine(CacheDir, "v1_location_cache.json"),
        Path.Combine(AppDataDir, "v1_location_cache.json"));

    public static string IconCacheDir => ResolveDirectoryPath(
        PrimaryIconsDir,
        LegacyIconsDir);

    public static string WallpapersCacheDir => ResolveDirectoryPath(
        PrimaryWallpapersDir,
        LegacyWallpapersDir);

    public static string RoverSoundsDir => ResolveDirectoryPath(
        PrimaryRoverSoundsDir,
        LegacyRoverSoundsDir);

    // Logs
    public static string MediaLogPath => ResolveFilePath(
        Path.Combine(LogsDir, "media_art.log"),
        Path.Combine(AppDataDir, "media_art.log"));

    public static string CrashLogPath => ResolveFilePath(
        Path.Combine(LogsDir, "crash.log"),
        Path.Combine(AppDataDir, "crash.log"));

    public static string HiddenDiagnosticsLogPath => ResolveFilePath(
        Path.Combine(LogsDir, "hidden_diagnostics.log"),
        Path.Combine(AppDataDir, "hidden_diagnostics.log"));

    // ── Resolution Helpers ───────────────────────────────────

    /// <summary>
    /// Resolves a file path:
    /// 1. If the categorized file already exists, return the categorized path.
    /// 2. If the legacy root file exists, return the legacy path (preserves existing users' files without changes).
    /// 3. Otherwise (first-time launch / clean state), return the categorized path for first-time saving.
    /// </summary>
    public static string ResolveFilePath(string categorizedPath, string legacyRootPath)
    {
        if (File.Exists(categorizedPath))
        {
            return categorizedPath;
        }

        if (File.Exists(legacyRootPath))
        {
            return legacyRootPath;
        }

        return categorizedPath;
    }

    /// <summary>
    /// Resolves a directory path:
    /// 1. If the categorized directory exists and contains entries, return it.
    /// 2. If the legacy directory exists and contains entries, return it.
    /// 3. Otherwise, return the categorized directory.
    /// </summary>
    public static string ResolveDirectoryPath(string categorizedDir, string legacyRootDir)
    {
        try
        {
            if (Directory.Exists(categorizedDir) && Directory.EnumerateFileSystemEntries(categorizedDir).Any())
            {
                return categorizedDir;
            }

            if (Directory.Exists(legacyRootDir) && Directory.EnumerateFileSystemEntries(legacyRootDir).Any())
            {
                return legacyRootDir;
            }
        }
        catch { }

        return categorizedDir;
    }

    /// <summary>
    /// Ensures that the parent directory for the given file or directory path exists.
    /// </summary>
    public static void EnsureDirectory(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch { }
    }
}
