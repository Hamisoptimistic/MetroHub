using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MetroHub.Core.Display;
using MetroHub.Core.Search.Native;
using MetroHub.Core.Services;

namespace MetroHub.Core.Search;

/// <summary>
/// Definition of a Windows Settings navigation target.
/// </summary>
public sealed record SettingsItem(
    string Title,
    string Uri,
    string Description,
    string[] Keywords);

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
    Func<ActionContext, Task<ActionResult>> ExecuteAsync);

/// <summary>
/// Static lookup tables for Windows settings, system commands, and web bang configurations.
/// </summary>
public static class PureMemoryData
{
    public static readonly SettingsItem[] WindowsSettings =
    [
        new("Bluetooth & devices", "ms-settings:bluetooth", "Manage Bluetooth and paired devices", ["bluetooth", "devices", "pair", "wireless"]),
        new("Wi-Fi & Network", "ms-settings:network-wifi", "Manage wireless networks and connections", ["wifi", "wi-fi", "internet", "wireless", "network"]),
        new("Ethernet Network", "ms-settings:network-ethernet", "Manage wired network connections", ["ethernet", "lan", "network", "internet"]),
        new("Display Settings", "ms-settings:display", "Resolution, scale, multiple displays", ["display", "resolution", "scale", "monitor", "screen", "brightness"]),
        new("Sound & Audio", "ms-settings:sound", "Volume levels, output and input devices", ["sound", "audio", "volume", "speakers", "microphone", "headphones"]),
        new("Windows Update", "ms-settings:windowsupdate", "Check for system and driver updates", ["windows update", "updates", "patch", "install updates"]),
        new("Installed Apps", "ms-settings:appsfeatures", "Manage and uninstall applications", ["apps", "installed apps", "uninstall", "programs", "applications"]),
        new("Storage & Disks", "ms-settings:storagesense", "Storage usage and cleanup recommendations", ["storage", "disk", "free space", "drive", "cleanup"]),
        new("Background & Wallpaper", "ms-settings:personalization-background", "Desktop background, slideshow, colors", ["background", "wallpaper", "theme", "personalization"]),
        new("Power & Battery", "ms-settings:powersleep", "Sleep timing, battery saver, power modes", ["power", "sleep", "battery", "energy", "screen off"]),
        new("Taskbar & Navigation", "ms-settings:taskbar", "Taskbar behaviors, icons, alignment", ["taskbar", "dock", "icons"]),
        new("Notifications & Focus", "ms-settings:notifications", "Notification banners and focus assist", ["notifications", "focus assist", "alerts", "do not disturb"]),
        new("Date & Time", "ms-settings:dateandtime", "Time zones, clock synchronization", ["date", "time", "clock", "timezone", "sync clock"]),
        new("Mouse & Touchpad", "ms-settings:mousetouchpad", "Pointer speed, scrolling, gestures", ["mouse", "touchpad", "cursor", "pointer"]),
        new("Printers & Scanners", "ms-settings:printers", "Manage printers and scanners", ["printers", "scanners", "print"]),
        new("Default Apps", "ms-settings:defaultapps", "Default browser, mail, media players", ["default apps", "defaults", "file associations"]),
        new("Privacy & Security", "ms-settings:privacy", "App permissions, camera, microphone", ["privacy", "security", "permissions", "camera", "microphone"]),
        new("Windows Security", "ms-settings:windowsdefender", "Antivirus, firewall, device health", ["security", "defender", "antivirus", "firewall"]),
        new("About System", "ms-settings:about", "Device specifications, processor, RAM, OS build", ["about", "specs", "system info", "device specifications", "ram"])
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
            }),

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
            }),

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
            }),

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
            }),

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
            }),

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
            })
    ];
}
