using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MetroHub.Core.Display;
using MetroHub.Core.Search.Native;
using MetroHub.Core.Services;

using Wpf.Ui.Controls;

namespace MetroHub.Core.Search;

/// <summary>
/// Definition of a Windows Settings navigation target.
/// </summary>
public sealed record SettingsItem(
    string Title,
    string Uri,
    string Description,
    string[] Keywords,
    SymbolRegular Icon = SymbolRegular.Settings24);

/// <summary>
/// Definition of an operating system power or maintenance command.
/// </summary>
public sealed record SystemCommandItem(
    string Id,
    string Title,
    string Description,
    string[] Keywords,
    bool RequiresConfirm,
    string? ConfirmPrompt,
    Func<ActionContext, Task<ActionResult>> ExecuteAsync,
    SymbolRegular Icon = SymbolRegular.Power24);

/// <summary>
/// Definition of a web search bang navigation target.
/// </summary>
public sealed record WebBangItem(
    string Prefix,
    string Name,
    string UrlTemplate,
    SymbolRegular Icon,
    string? HomeUrl = null);

/// <summary>
/// Definition of an interactive search capability hint or shortcut.
/// </summary>
public sealed record SearchTipItem(
    string Prefix,
    string Title,
    string Description,
    SymbolRegular Icon,
    string[] Keywords);

/// <summary>
/// Static lookup tables for Windows settings, system commands, and web bang configurations.
/// </summary>
public static class PureMemoryData
{
    public static readonly SettingsItem[] WindowsSettings =
    [
        new("Bluetooth & devices", "ms-settings:bluetooth", "Manage Bluetooth and paired devices", ["bluetooth", "devices", "pair", "wireless"], SymbolRegular.Bluetooth24),
        new("Wi-Fi & Network", "ms-settings:network-wifi", "Manage wireless networks and connections", ["wifi", "wi-fi", "internet", "wireless", "network"], SymbolRegular.Wifi124),
        new("Ethernet Network", "ms-settings:network-ethernet", "Manage wired network connections", ["ethernet", "lan", "network", "internet"], SymbolRegular.Wifi124),
        new("Display Settings", "ms-settings:display", "Resolution, scale, multiple displays", ["display", "resolution", "scale", "monitor", "screen", "brightness"], SymbolRegular.Desktop24),
        new("Sound & Audio", "ms-settings:sound", "Volume levels, output and input devices", ["sound", "audio", "volume", "speakers", "microphone", "headphones"], SymbolRegular.Speaker224),
        new("Windows Update", "ms-settings:windowsupdate", "Check for system and driver updates", ["windows update", "updates", "patch", "install updates"], SymbolRegular.Settings24),
        new("Installed Apps", "ms-settings:appsfeatures", "Manage and uninstall applications", ["apps", "installed apps", "uninstall", "programs", "applications"], SymbolRegular.Settings24),
        new("Storage & Disks", "ms-settings:storagesense", "Storage usage and cleanup recommendations", ["storage", "disk", "free space", "drive", "cleanup"], SymbolRegular.Settings24),
        new("Background & Wallpaper", "ms-settings:personalization-background", "Desktop background, slideshow, colors", ["background", "wallpaper", "theme", "personalization"], SymbolRegular.Settings24),
        new("Power & Battery", "ms-settings:powersleep", "Sleep timing, battery saver, power modes", ["power", "sleep", "battery", "energy", "screen off"], SymbolRegular.BatteryCharge24),
        new("Taskbar & Navigation", "ms-settings:taskbar", "Taskbar behaviors, icons, alignment", ["taskbar", "dock", "icons"], SymbolRegular.Settings24),
        new("Notifications & Focus", "ms-settings:notifications", "Notification banners and focus assist", ["notifications", "focus assist", "alerts", "do not disturb"], SymbolRegular.Settings24),
        new("Date & Time", "ms-settings:dateandtime", "Time zones, clock synchronization", ["date", "time", "clock", "timezone", "sync clock"], SymbolRegular.Settings24),
        new("Mouse & Touchpad", "ms-settings:mousetouchpad", "Pointer speed, scrolling, gestures", ["mouse", "touchpad", "cursor", "pointer"], SymbolRegular.Settings24),
        new("Printers & Scanners", "ms-settings:printers", "Manage printers and scanners", ["printers", "scanners", "print"], SymbolRegular.Settings24),
        new("Default Apps", "ms-settings:defaultapps", "Default browser, mail, media players", ["default apps", "defaults", "file associations"], SymbolRegular.Settings24),
        new("Privacy & Security", "ms-settings:privacy", "App permissions, camera, microphone", ["privacy", "security", "permissions", "camera", "microphone"], SymbolRegular.Settings24),
        new("Windows Security", "ms-settings:windowsdefender", "Antivirus, firewall, device health", ["security", "defender", "antivirus", "firewall"], SymbolRegular.Settings24),
        new("About System", "ms-settings:about", "Device specifications, processor, RAM, OS build", ["about", "specs", "system info", "device specifications", "ram"], SymbolRegular.Settings24)
    ];

    public static readonly WebBangItem[] WebBangs =
    [
        new("g", "Google", "https://www.google.com/search?q=", SymbolRegular.Search24, "https://www.google.com"),
        new("yt", "YouTube", "https://www.youtube.com/results?search_query=", SymbolRegular.Play24, "https://www.youtube.com"),
        new("gh", "GitHub", "https://github.com/search?q=", SymbolRegular.Code24, "https://github.com"),
        new("ddg", "DuckDuckGo", "https://duckduckgo.com/?q=", SymbolRegular.GlobeSearch24, "https://duckduckgo.com")
    ];

    public static readonly SearchTipItem[] SearchTips =
    [
        new("> ", "> <command>", "Run command in terminal (e.g. '> ping 1.1.1.1')", SymbolRegular.WindowConsole20, ["terminal", "cmd", "powershell", "shell", "run", "command"]),
        new("set: ", "set: <setting>", "Search Windows settings (e.g. 'set: bluetooth')", SymbolRegular.Settings24, ["settings", "windows settings", "preferences", "config", "control"]),
        new("kill ", "kill <process>", "Terminate running process by name (e.g. 'kill notepad')", SymbolRegular.DismissCircle24, ["kill", "terminate", "close", "taskkill", "stop", "end process"]),
        new("win ", "win <window>", "Switch to active window (e.g. 'win chrome')", SymbolRegular.Window24, ["window", "switch", "open windows", "active"]),
        new("time ", "time <city>", "Show world clock for city (e.g. 'time tokyo')", SymbolRegular.Clock24, ["time", "clock", "world clock", "city", "timezone"]),
        new("vol ", "vol <level>", "Set audio volume or mute (e.g. 'vol 50', 'vol mute')", SymbolRegular.Speaker224, ["volume", "sound", "audio", "speaker", "mute", "vol"]),
        new("cb ", "cb <query>", "Search clipboard history (e.g. 'cb code')", SymbolRegular.ClipboardPaste24, ["clipboard", "copy", "paste", "history", "clip", "cb"]),
        new("case ", "case <text>", "Convert text casing (e.g. 'case hello world')", SymbolRegular.TextCaseTitle24, ["case", "text", "uppercase", "lowercase", "camelcase", "slug"]),
        new("calc ", "calc <math>", "Evaluate math expressions (e.g. '15 * 4.5 + 20')", SymbolRegular.Calculator24, ["calc", "math", "calculator", "formula", "eval"]),
        new("conv ", "conv <units>", "Convert units / currencies (e.g. '50 kg to lbs', '100 eur to usd')", SymbolRegular.ArrowSwap24, ["convert", "units", "currency", "converter", "measurement"]),
        new("g ", "g <query>", "Search with Google (e.g. 'g fluent ui')", SymbolRegular.Search24, ["google", "web search", "browse", "internet", "g"]),
        new("yt ", "yt <query>", "Search YouTube (e.g. 'yt lofi hip hop')", SymbolRegular.Play24, ["youtube", "video", "music", "stream", "yt"]),
        new("gh ", "gh <query>", "Search GitHub repositories (e.g. 'gh metrohub')", SymbolRegular.Code24, ["github", "git", "repo", "repository", "code", "gh"]),
        new("ddg ", "ddg <query>", "Search DuckDuckGo (e.g. 'ddg privacy')", SymbolRegular.GlobeSearch24, ["duckduckgo", "ddg", "private search"]),
    ];

    public static readonly SystemCommandItem[] SystemCommands =
    [
        new(
            Id: "sys:lock",
            Title: "Lock workstation",
            Description: "Lock current Windows user session",
            Keywords: ["lock", "lock workstation", "lock screen"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: ctx =>
            {
                _ = CinematicFadeService.Instance.FadeAndExecuteAsync(() => CinematicFadeService.LockWorkStation());
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.LockClosed24),

        new(
            Id: "sys:sleep",
            Title: "Sleep",
            Description: "Suspend computer and put to low power mode",
            Keywords: ["sleep", "suspend", "standby"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: ctx =>
            {
                _ = CinematicFadeService.Instance.FadeAndExecuteAsync(() => CinematicFadeService.SetSuspendState(false, true, false));
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.WeatherMoon24),

        new(
            Id: "sys:restart",
            Title: "Restart computer",
            Description: "Reboot Windows operating system",
            Keywords: ["restart", "reboot"],
            RequiresConfirm: true,
            ConfirmPrompt: "Press Enter again to restart",
            ExecuteAsync: ctx =>
            {
                if (!ctx.Confirmed)
                {
                    return Task.FromResult(new ActionResult(ActionOutcome.RequireConfirmation, "Press Enter again to restart"));
                }

                ProcessLauncherService.LaunchTarget("shutdown.exe", "/r /t 0");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.ArrowCounterclockwise24),

        new(
            Id: "sys:shutdown",
            Title: "Shut down computer",
            Description: "Power off Windows operating system",
            Keywords: ["shutdown", "shut down", "power off", "turn off"],
            RequiresConfirm: true,
            ConfirmPrompt: "Press Enter again to shut down",
            ExecuteAsync: ctx =>
            {
                if (!ctx.Confirmed)
                {
                    return Task.FromResult(new ActionResult(ActionOutcome.RequireConfirmation, "Press Enter again to shut down"));
                }

                ProcessLauncherService.LaunchTarget("shutdown.exe", "/s /t 0");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Power24),

        new(
            Id: "sys:signout",
            Title: "Sign out",
            Description: "Close current session and sign out",
            Keywords: ["sign out", "signout", "log off", "logoff"],
            RequiresConfirm: true,
            ConfirmPrompt: "Press Enter again to sign out",
            ExecuteAsync: ctx =>
            {
                if (!ctx.Confirmed)
                {
                    return Task.FromResult(new ActionResult(ActionOutcome.RequireConfirmation, "Press Enter again to sign out"));
                }

                ProcessLauncherService.LaunchTarget("shutdown.exe", "/l");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.SignOut24),

        new(
            Id: "sys:recycle",
            Title: "Empty Recycle Bin",
            Description: "Permanently delete files in the Recycle Bin",
            Keywords: ["empty recycle bin", "recycle bin", "trash", "empty trash"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                try
                {
                    NativeMethodsSearch.SHEmptyRecycleBin(
                        IntPtr.Zero,
                        null,
                        NativeMethodsSearch.SHERB_NOCONFIRMATION |
                        NativeMethodsSearch.SHERB_NOPROGRESSUI |
                        NativeMethodsSearch.SHERB_NOSOUND);
                    return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, "Recycle Bin emptied"));
                }
                catch (Exception ex)
                {
                    Safe.Log("SystemCommands.EmptyRecycleBin", ex);
                    return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, "Failed to empty Recycle Bin"));
                }
            },
            Icon: SymbolRegular.Delete24)
    ];
}
