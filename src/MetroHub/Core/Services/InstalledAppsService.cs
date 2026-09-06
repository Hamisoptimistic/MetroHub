using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

/// <summary>
/// Modular catalog provider for installed applications.
/// Features high-speed disk caching (< 2ms cold start), background live sync with shell:AppsFolder,
/// and real-time filesystem watching on Windows Start Menu directories for instant new app detection.
/// </summary>
public static class InstalledAppsService
{
    private static List<CatalogItemModel>? _cachedApps;
    private static readonly object _lock = new();
    private static readonly List<FileSystemWatcher> _watchers = new();
    private static Timer? _debounceTimer;

    public static event Action<List<CatalogItemModel>>? AppsCatalogChanged;

    private static readonly string[] ExcludedKeywords = new[]
    {
        "uninstall", "unins000", "remove", "documentation", "help",
        "readme", "manual", "release notes", "what's new", "license",
        "crash report", "feedback", "diagnostics", "troubleshoot"
    };

    private static readonly string[] ExcludedExtensions = new[]
    {
        ".chm", ".url", ".htm", ".html", ".txt", ".ini", ".pdf", ".rtf"
    };

    static InstalledAppsService()
    {
        InitStartMenuWatchers();
    }

    private static void InitStartMenuWatchers()
    {
        try
        {
            var paths = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };

            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    var watcher = new FileSystemWatcher(path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                        Filter = "*.*",
                        EnableRaisingEvents = true
                    };

                    watcher.Created += OnStartMenuFolderChanged;
                    watcher.Deleted += OnStartMenuFolderChanged;
                    watcher.Renamed += OnStartMenuFolderChanged;

                    _watchers.Add(watcher);
                }
            }
        }
        catch { }
    }

    private static void OnStartMenuFolderChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: Installers usually write multiple shortcut files within a few hundred ms.
        // Wait 1500ms after the last write event before kicking off background enumeration.
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                _ = GetInstalledAppsAsync(forceRefresh: true);
            }, null, 1500, Timeout.Infinite);
        }
    }

    public static Task<List<CatalogItemModel>> GetInstalledAppsAsync(bool forceRefresh = false)
    {
        return Task.Run(() => GetInstalledApps(forceRefresh));
    }

    public static List<CatalogItemModel> GetInstalledApps(bool forceRefresh = false)
    {
        lock (_lock)
        {
            // 1. If not forcing refresh, return in-memory cache if available
            if (!forceRefresh)
            {
                if (_cachedApps != null)
                {
                    return _cachedApps;
                }

                // 2. Try fast loading from disk cache (~1-2ms cold start)
                var diskCache = StorageService.LoadAppsCache();
                if (diskCache != null && diskCache.Count > 0)
                {
                    _cachedApps = diskCache;
                    return _cachedApps;
                }
            }

            var previousApps = _cachedApps ?? StorageService.LoadAppsCache();
            var appMap = new Dictionary<string, CatalogItemModel>(StringComparer.OrdinalIgnoreCase);

            // Pre-index Start Menu shortcuts to map AUMIDs, MSI shortcuts, and auto-generated IDs to actual .lnk files
            var startMenuLnkMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var folder in new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
                {
                    if (Directory.Exists(folder))
                    {
                        foreach (var lnk in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories))
                        {
                            string baseName = Path.GetFileNameWithoutExtension(lnk);
                            startMenuLnkMap.TryAdd(baseName, lnk);
                        }
                    }
                }
            }
            catch { }

            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType != null)
                {
                    dynamic? shell = Activator.CreateInstance(shellType);
                    if (shell != null)
                    {
                        dynamic appsFolder = shell.NameSpace("shell:AppsFolder");
                        if (appsFolder != null)
                        {
                            dynamic items = appsFolder.Items();
                            int count = items.Count;

                            for (int i = 0; i < count; i++)
                            {
                                try
                                {
                                    dynamic item = items.Item(i);
                                    string name = item.Name?.ToString() ?? string.Empty;
                                    string path = item.Path?.ToString() ?? string.Empty;

                                    if (IsValidApplication(name, path))
                                    {
                                        // 1. Resolve Known Folder GUIDs (e.g. {6D809377-...} -> C:\Program Files\...)
                                        string kf = IconExtractorService.ResolveKnownFolderGuid(path);
                                        if (File.Exists(kf) || Directory.Exists(kf))
                                        {
                                            path = kf;
                                        }
                                        // 2. Map auto-generated IDs or unrooted items to physical Start Menu .lnk
                                        else if (startMenuLnkMap.TryGetValue(name, out string? lnk) && File.Exists(lnk))
                                        {
                                            path = lnk;
                                        }
                                        // 3. Standardize AUMIDs & shell folder items to full shell URI for icon extraction & launching
                                        else if (!path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) &&
                                            (!Path.IsPathRooted(path) || path.StartsWith("{") || !File.Exists(path)))
                                        {
                                            path = @"shell:AppsFolder\" + path;
                                        }

                                        if (!appMap.ContainsKey(name))
                                        {
                                            appMap[name] = new CatalogItemModel
                                            {
                                                Name = name,
                                                TargetPath = path,
                                                TileType = TileType.App,
                                                SpanX = 2,
                                                SpanY = 2,
                                                ProviderId = "installed_apps"
                                            };
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        Marshal.FinalReleaseComObject(shell);
                    }
                }
            }
            catch { }

            var freshApps = appMap.Values
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            // Check if anything actually changed compared to the previous state
            bool hasChanged = previousApps == null || previousApps.Count != freshApps.Count;
            if (!hasChanged && previousApps != null)
            {
                for (int i = 0; i < freshApps.Count; i++)
                {
                    if (!string.Equals(previousApps[i].TargetPath, freshApps[i].TargetPath, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(previousApps[i].Name, freshApps[i].Name, StringComparison.Ordinal))
                    {
                        hasChanged = true;
                        break;
                    }
                }
            }

            _cachedApps = freshApps;

            // Persist to disk cache whenever fresh enumeration runs or changes are detected
            StorageService.SaveAppsCache(_cachedApps);

            if (hasChanged)
            {
                AppsCatalogChanged?.Invoke(_cachedApps);
            }

            return _cachedApps;
        }
    }

    private static bool IsValidApplication(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) return false;

        string nameLower = name.ToLowerInvariant();
        string pathLower = path.ToLowerInvariant();

        // Filter out junk keywords
        foreach (var keyword in ExcludedKeywords)
        {
            if (nameLower.Contains(keyword) || pathLower.Contains(keyword)) return false;
        }

        // Filter out documentation and web links
        foreach (var ext in ExcludedExtensions)
        {
            if (pathLower.EndsWith(ext)) return false;
        }

        // Filter out folders/directories
        if (Directory.Exists(path)) return false;

        return true;
    }
}
