using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MetroHub.Core.Common;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Messaging;
using Wpf.Ui.Controls;

namespace MetroHub.Core.Search;

/// <summary>
/// Central registry decoupling presentation-layer pinning and catalog operations
/// from search candidate action execution.
/// </summary>
public static class SearchActionRegistry
{
    public static Func<string, bool>? IsFilePinned { get; set; }
    public static Action<string>? PinFile { get; set; }
    public static Action<string>? UnpinFile { get; set; }

    public static Func<CatalogItemModel, bool>? IsAppPinned { get; set; }
    public static Action<CatalogItemModel>? PinApp { get; set; }
    public static Action<CatalogItemModel>? UnpinApp { get; set; }
    public static Action<CatalogItemModel>? LaunchApp { get; set; }
    public static Action<CatalogItemModel>? LaunchAppAsAdmin { get; set; }
    public static Action<CatalogItemModel>? UninstallApp { get; set; }
}

/// <summary>
/// Provides universal, lazily evaluated file and directory actions for search candidates.
/// Evaluates pin states and executable permissions dynamically at right-click time.
/// </summary>
public static class FileActionProvider
{
    public static CandidateAction CreateOpenAction(string fullPath)
    {
        return new CandidateAction(
            Id: "file:open",
            Title: "Open",
            KeyGestureHint: "Enter",
            Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.ArrowRight24),
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget(fullPath);
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            });
    }

    public static IReadOnlyList<CandidateAction> GetActions(Candidate candidate)
    {
        string path = candidate.FullPathOrKey;
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<CandidateAction>();

        bool isFolder = candidate.IsFolder;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool isScriptOrExe = !isFolder && ext is ".exe" or ".bat" or ".cmd" or ".ps1" or ".msc" or ".lnk";

        var actions = new List<CandidateAction>(8);

        // Group 1: Launch Actions
        actions.Add(CreateOpenAction(path));

        if (PowerToysPeekLauncher.IsAvailable)
        {
            actions.Add(new CandidateAction(
                Id: "file:peek",
                Title: "Preview (Peek)",
                KeyGestureHint: "Space",
                Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Eye24),
                ExecuteAsync: _ =>
                {
                    PowerToysPeekLauncher.TryLaunchPreview(path);
                    return Task.FromResult(new ActionResult(ActionOutcome.StayOpenAndRefresh));
                }));
        }

        if (isFolder)
        {
            actions.Add(new CandidateAction(
                Id: "file:open_terminal",
                Title: "Open in Terminal",
                KeyGestureHint: null,
                Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Code24),
                ExecuteAsync: _ =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo("cmd.exe")
                        {
                            WorkingDirectory = path,
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                    }
                    catch (Exception ex)
                    {
                        Safe.Log("FileActionProvider.OpenTerminal", ex);
                    }
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                }));
        }

        if (isScriptOrExe)
        {
            actions.Add(new CandidateAction(
                Id: "file:run_admin",
                Title: "Run as administrator",
                KeyGestureHint: null,
                Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Shield24),
                ExecuteAsync: _ =>
                {
                    string target = ext == ".lnk" ? IconExtractorService.ResolveExecutableTarget(path) : path;
                    ProcessLauncherService.LaunchTarget(target, runAsAdmin: true);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                }));
        }

        // Group 2: Location & Clipboard (BeginGroup = true)
        actions.Add(new CandidateAction(
            Id: "file:open_location",
            Title: isFolder ? "Open folder location" : "Open file location",
            KeyGestureHint: null,
            Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.FolderOpen24),
            BeginGroup: true,
            ExecuteAsync: _ =>
            {
                try
                {
                    if (File.Exists(path))
                    {
                        Process.Start("explorer.exe", $"/select,\"{path}\"");
                    }
                    else if (Directory.Exists(path))
                    {
                        Process.Start("explorer.exe", $"\"{path}\"");
                    }
                }
                catch (Exception ex)
                {
                    Safe.Log("FileActionProvider.OpenLocation", ex);
                }
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            }));

        actions.Add(new CandidateAction(
            Id: "file:copy_path",
            Title: "Copy path",
            KeyGestureHint: "Ctrl+Shift+C",
            Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Copy24),
            ExecuteAsync: _ =>
            {
                ClipboardHelper.SetClipboardText(path);
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            }));

        // Group 3: Dynamic Canvas & Sidebar Pinning (BeginGroup = true)
        bool isPinnedMetroHub = SearchActionRegistry.IsFilePinned?.Invoke(path) ?? false;
        actions.Add(new CandidateAction(
            Id: "file:pin_metrohub",
            Title: isPinnedMetroHub ? "Unpin from MetroHub" : "Pin to MetroHub",
            KeyGestureHint: null,
            Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: isPinnedMetroHub ? SymbolRegular.PinOff24 : SymbolRegular.Pin24),
            IsDestructive: isPinnedMetroHub,
            BeginGroup: true,
            ExecuteAsync: _ =>
            {
                if (isPinnedMetroHub)
                {
                    SearchActionRegistry.UnpinFile?.Invoke(path);
                }
                else
                {
                    SearchActionRegistry.PinFile?.Invoke(path);
                }
                return Task.FromResult(new ActionResult(ActionOutcome.StayOpenAndRefresh));
            }));

        var rail = CanvasMessenger.GetSidebarRail();
        bool isPinnedSidebar = rail != null && SidebarPinningService.IsPathPinnedToSidebar(rail, path);
        actions.Add(new CandidateAction(
            Id: "file:pin_sidebar",
            Title: isPinnedSidebar ? "Unpin from Sidebar" : "Pin to Sidebar",
            KeyGestureHint: null,
            Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: isPinnedSidebar ? SymbolRegular.PinOff24 : SymbolRegular.Pin24),
            IsDestructive: isPinnedSidebar,
            ExecuteAsync: _ =>
            {
                var r = CanvasMessenger.GetSidebarRail();
                if (r != null)
                {
                    if (isPinnedSidebar)
                    {
                        SidebarPinningService.UnpinPathFromSidebar(r, path);
                    }
                    else
                    {
                        string smartIcon = isFolder
                            ? "Folder24"
                            : SidebarPinningService.GetDefaultFileSymbol(Path.GetExtension(path));

                        string? iconPath = null;
                        if (!isFolder)
                        {
                            try { iconPath = IconExtractorService.ExtractAndCacheIcon(path); } catch { }
                        }

                        var shortcut = new SidebarShortcutItem
                        {
                            Title = candidate.DisplayName,
                            Target = path,
                            TargetType = isFolder ? SidebarShortcutType.CustomFolder : SidebarShortcutType.Application,
                            IconSymbol = smartIcon,
                            CustomIconPath = iconPath,
                            SortOrder = r.Shortcuts.Count,
                            IsRemovable = true
                        };
                        r.AddShortcutItem(shortcut);
                    }
                }
                return Task.FromResult(new ActionResult(ActionOutcome.StayOpenAndRefresh));
            }));

        return actions;
    }
}

/// <summary>
/// Provides universal, lazily evaluated application actions for installed app search candidates.
/// Evaluates live canvas pin and sidebar states dynamically at right-click time.
/// </summary>
public static class AppActionProvider
{
    public static CandidateAction CreateOpenAction(CatalogItemModel app)
    {
        return new CandidateAction(
            Id: "app:open",
            Title: "Open",
            KeyGestureHint: "Enter",
            Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.ArrowRight24),
            ExecuteAsync: _ =>
            {
                SearchActionRegistry.LaunchApp?.Invoke(app);
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            });
    }

    public static IReadOnlyList<CandidateAction> GetActions(Candidate candidate)
    {
        if (candidate.Tag is not CatalogItemModel app) return Array.Empty<CandidateAction>();

        var actions = new List<CandidateAction>(6);

        // Group 1: Launch Actions
        actions.Add(CreateOpenAction(app));

        if (!string.IsNullOrEmpty(app.TargetPath))
        {
            actions.Add(new CandidateAction(
                Id: "app:run_admin",
                Title: "Run as administrator",
                KeyGestureHint: null,
                Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Shield24),
                ExecuteAsync: _ =>
                {
                    if (SearchActionRegistry.LaunchAppAsAdmin != null)
                    {
                        SearchActionRegistry.LaunchAppAsAdmin.Invoke(app);
                    }
                    else
                    {
                        string execPath = IconExtractorService.ResolveExecutableTarget(app.TargetPath);
                        ProcessLauncherService.LaunchTarget(execPath, app.Arguments, runAsAdmin: true);
                    }
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                }));
        }

        if (!string.IsNullOrEmpty(app.TargetPath) && (File.Exists(app.TargetPath) || Directory.Exists(app.TargetPath)))
        {
            actions.Add(new CandidateAction(
                Id: "app:open_location",
                Title: "Open file location",
                KeyGestureHint: null,
                Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.FolderOpen24),
                ExecuteAsync: _ =>
                {
                    try
                    {
                        if (File.Exists(app.TargetPath))
                        {
                            Process.Start("explorer.exe", $"/select,\"{app.TargetPath}\"");
                        }
                        else
                        {
                            Process.Start("explorer.exe", $"\"{app.TargetPath}\"");
                        }
                    }
                    catch (Exception ex)
                    {
                        Safe.Log("AppActionProvider.OpenLocation", ex);
                    }
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                }));
        }

        // Group 2: Dynamic Canvas & Sidebar Pinning (BeginGroup = true)
        bool isPinned = SearchActionRegistry.IsAppPinned?.Invoke(app) ?? false;
        actions.Add(new CandidateAction(
            Id: "app:pin_metrohub",
            Title: isPinned ? "Unpin from MetroHub" : "Pin to MetroHub",
            KeyGestureHint: null,
            Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: isPinned ? SymbolRegular.PinOff24 : SymbolRegular.Pin24),
            IsDestructive: isPinned,
            BeginGroup: true,
            ExecuteAsync: _ =>
            {
                if (isPinned)
                {
                    SearchActionRegistry.UnpinApp?.Invoke(app);
                }
                else
                {
                    SearchActionRegistry.PinApp?.Invoke(app);
                }
                return Task.FromResult(new ActionResult(ActionOutcome.StayOpenAndRefresh));
            }));

        var rail = CanvasMessenger.GetSidebarRail();
        bool isPinnedSidebar = rail != null && SidebarPinningService.IsCatalogItemPinnedToSidebar(rail, app);
        actions.Add(new CandidateAction(
            Id: "app:pin_sidebar",
            Title: isPinnedSidebar ? "Unpin from Sidebar" : "Pin to Sidebar",
            KeyGestureHint: null,
            Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: isPinnedSidebar ? SymbolRegular.PinOff24 : SymbolRegular.Pin24),
            IsDestructive: isPinnedSidebar,
            ExecuteAsync: _ =>
            {
                var r = CanvasMessenger.GetSidebarRail();
                if (r != null)
                {
                    if (isPinnedSidebar)
                    {
                        SidebarPinningService.UnpinCatalogItemFromSidebar(r, app);
                    }
                    else
                    {
                        SidebarPinningService.PinCatalogItem(r, app);
                    }
                }
                return Task.FromResult(new ActionResult(ActionOutcome.StayOpenAndRefresh));
            }));

        // Group 3: App Uninstall (BeginGroup = true)
        actions.Add(new CandidateAction(
            Id: "app:uninstall",
            Title: "Uninstall",
            KeyGestureHint: null,
            Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Delete24),
            IsDestructive: true,
            BeginGroup: true,
            ExecuteAsync: _ =>
            {
                SearchActionRegistry.UninstallApp?.Invoke(app);
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            }));

        return actions;
    }
}
