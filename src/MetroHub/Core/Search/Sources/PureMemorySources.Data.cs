using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using MetroHub.Core.Display;
using MetroHub.Core.Search.Native;
using MetroHub.Core.Services;
using Microsoft.Win32;
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
    string[] Keywords,
    string Section = "Shortcuts & Tips");

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
        new("Installed Apps", "ms-settings:appsfeatures", "Manage and uninstall applications", ["apps", "installed apps", "uninstall", "programs", "applications"], SymbolRegular.Apps24),
        new("Storage & Disks", "ms-settings:storagesense", "Storage usage and cleanup recommendations", ["storage", "disk", "free space", "drive", "cleanup"], SymbolRegular.Folder24),
        new("Background & Wallpaper", "ms-settings:personalization-background", "Desktop background, slideshow, colors", ["background", "wallpaper", "theme", "personalization"], SymbolRegular.Image24),
        new("Colors & Themes", "ms-settings:personalization-colors", "Accent color, light and dark mode", ["color", "accent", "dark mode", "light mode", "theme"], SymbolRegular.Color24),
        new("Power & Battery", "ms-settings:powersleep", "Sleep timing, battery saver, power modes", ["power", "sleep", "battery", "energy", "screen off"], SymbolRegular.BatteryCharge24),
        new("Taskbar & Navigation", "ms-settings:taskbar", "Taskbar behaviors, icons, alignment", ["taskbar", "dock", "icons"], SymbolRegular.Desktop24),
        new("Notifications & Focus", "ms-settings:notifications", "Notification banners and focus assist", ["notifications", "focus assist", "alerts", "do not disturb"], SymbolRegular.DismissCircle24),
        new("Date & Time", "ms-settings:dateandtime", "Time zones, clock synchronization", ["date", "time", "clock", "timezone", "sync clock"], SymbolRegular.Clock24),
        new("Mouse & Touchpad", "ms-settings:mousetouchpad", "Pointer speed, scrolling, gestures", ["mouse", "touchpad", "cursor", "pointer"], SymbolRegular.Desktop24),
        new("Printers & Scanners", "ms-settings:printers", "Manage printers and scanners", ["printers", "scanners", "print"], SymbolRegular.Document24),
        new("Default Apps", "ms-settings:defaultapps", "Default browser, mail, media players", ["default apps", "defaults", "file associations"], SymbolRegular.Apps24),
        new("Privacy & Security", "ms-settings:privacy", "App permissions, camera, microphone", ["privacy", "security", "permissions", "camera", "microphone"], SymbolRegular.Shield32),
        new("Windows Security", "ms-settings:windowsdefender", "Antivirus, firewall, device health", ["security", "defender", "antivirus", "firewall"], SymbolRegular.Shield32),
        new("Developer Settings", "ms-settings:developers", "Developer mode, remote desktop, device discovery", ["developer", "developer mode", "dev", "sideload"], SymbolRegular.Code24),
        new("Optional Features", "ms-settings:optionalfeatures", "Manage Windows optional features and capabilities", ["optional features", "features", "wsl", "hyper-v"], SymbolRegular.Settings24),
        new("Activation", "ms-settings:activation", "Windows activation state and product key", ["activation", "license", "product key", "windows key"], SymbolRegular.CheckmarkCircle24),
        new("About System", "ms-settings:about", "Device specifications, processor, RAM, OS build", ["about", "specs", "system info", "device specifications", "ram"], SymbolRegular.Info24)
    ];

    public static readonly WebBangItem[] WebBangs =
    [
        new("g", "Google", "https://www.google.com/search?q=", SymbolRegular.Search24, "https://www.google.com"),
        new("yt", "YouTube", "https://www.youtube.com/results?search_query=", SymbolRegular.Play24, "https://www.youtube.com"),
        new("gh", "GitHub", "https://github.com/search?q=", SymbolRegular.Code24, "https://github.com"),
        new("ddg", "DuckDuckGo", "https://duckduckgo.com/?q=", SymbolRegular.GlobeSearch24, "https://duckduckgo.com"),
        new("w", "Wikipedia", "https://en.wikipedia.org/wiki/Special:Search?search=", SymbolRegular.Book24, "https://en.wikipedia.org"),
        new("r", "Reddit", "https://www.reddit.com/search/?q=", SymbolRegular.Globe24, "https://www.reddit.com"),
        new("so", "StackOverflow", "https://stackoverflow.com/search?q=", SymbolRegular.Code24, "https://stackoverflow.com"),
        new("npm", "npm", "https://www.npmjs.com/search?q=", SymbolRegular.Cube24, "https://www.npmjs.com"),
        new("nuget", "NuGet", "https://www.nuget.org/packages?q=", SymbolRegular.Cube24, "https://www.nuget.org"),
        new("crates", "Crates.io", "https://crates.io/search?q=", SymbolRegular.Cube24, "https://crates.io"),
        new("maps", "Google Maps", "https://www.google.com/maps/search/", SymbolRegular.Location24, "https://maps.google.com"),
        new("amz", "Amazon", "https://www.amazon.com/s?k=", SymbolRegular.Briefcase24, "https://www.amazon.com"),
        new("ai", "ChatGPT", "https://chatgpt.com/?q=", SymbolRegular.Sparkle24, "https://chatgpt.com")
    ];

    public static readonly SearchTipItem[] SearchTips =
    [
        // 1. Windows & System Tools
        new("> ", "> ping 1.1.1.1", "Run in Command Prompt or PowerShell", SymbolRegular.WindowConsole20, ["terminal", "cmd", "powershell", "shell", "run", "command"], "Windows & System Tools"),
        new("set: ", "set: bluetooth", "Open Windows settings or control panel", SymbolRegular.Settings24, ["settings", "windows settings", "preferences", "config", "control"], "Windows & System Tools"),
        new("kill ", "kill notepad", "Force close a running app or process", SymbolRegular.DismissCircle24, ["kill", "terminate", "close", "taskkill", "stop", "end process"], "Windows & System Tools"),
        new("win ", "win chrome", "Switch to an open window", SymbolRegular.Window24, ["window", "switch", "open windows", "active"], "Windows & System Tools"),
        new("time ", "time tokyo", "Check current time in any world city", SymbolRegular.Clock24, ["time", "clock", "world clock", "city", "timezone"], "Windows & System Tools"),
        new("vol ", "vol 50", "Set volume level or mute audio", SymbolRegular.Speaker224, ["volume", "sound", "audio", "speaker", "mute", "vol"], "Windows & System Tools"),
        new("cb ", "cb search", "Search and paste clipboard history", SymbolRegular.ClipboardPaste24, ["clipboard", "copy", "paste", "history", "clip", "cb"], "Windows & System Tools"),

        // 2. Everyday Math & Units
        new("150 + 18%", "150 + 18%", "Calculate math, percentages, and formulas", SymbolRegular.Calculator24, ["calc", "math", "calculator", "formula", "eval", "percentage"], "Everyday Math & Units"),
        new("100 f to c", "100 f to c", "Convert temperature, weight, length, or speed", SymbolRegular.ArrowSwap24, ["convert", "units", "currency", "converter", "measurement"], "Everyday Math & Units"),

        // 3. Developer Utilities
        new("case ", "case myVariable", "Convert to camelCase, snake_case, kebab-case", SymbolRegular.TextCaseTitle24, ["case", "text", "uppercase", "lowercase", "camelcase", "slug"], "Developer Utilities"),
        new("guid", "guid", "Generate and copy a new UUID", SymbolRegular.Code24, ["guid", "uuid", "id", "random"], "Developer Utilities"),
        new("b64 ", "b64 encode hello", "Encode or decode Base64 text", SymbolRegular.Code24, ["base64", "b64", "encode", "decode"], "Developer Utilities"),
        new("url ", "url encode hello", "Escape or unescape URL characters", SymbolRegular.Globe24, ["url", "uri", "encode", "decode", "escape"], "Developer Utilities"),
        new("sha256 ", "sha256 password", "Generate SHA-256, SHA-1, or MD5 hash", SymbolRegular.Shield32, ["hash", "md5", "sha256", "sha1", "crypto"], "Developer Utilities"),
        new("#0078D7", "#0078D7", "Convert color to HEX, RGB, HSL, and XAML", SymbolRegular.Color24, ["color", "hex", "rgb", "hsl", "brush"], "Developer Utilities"),

        // 4. Web Search Bangs
        new("w ", "w space station", "Search Wikipedia", SymbolRegular.Book24, ["wikipedia", "wiki", "w", "encyclopedia"], "Web Search Bangs"),
        new("r ", "r mechanical keyboards", "Search Reddit communities", SymbolRegular.Globe24, ["reddit", "r", "community"], "Web Search Bangs"),
        new("so ", "so wpf data binding", "Search StackOverflow answers", SymbolRegular.Code24, ["stackoverflow", "so", "questions", "dev"], "Web Search Bangs"),
        new("npm ", "npm express", "Search npm package registry", SymbolRegular.Cube24, ["npm", "package", "node", "javascript"], "Web Search Bangs"),
        new("nuget ", "nuget communitytoolkit", "Search NuGet packages", SymbolRegular.Cube24, ["nuget", "package", "dotnet", "c#"], "Web Search Bangs"),
        new("crates ", "crates tokio", "Search Rust crates", SymbolRegular.Cube24, ["crates", "rust", "package"], "Web Search Bangs"),
        new("maps ", "maps tokyo tower", "Search Google Maps locations", SymbolRegular.Location24, ["maps", "google maps", "directions", "location"], "Web Search Bangs"),
        new("amz ", "amz usb-c cable", "Search products on Amazon", SymbolRegular.Briefcase24, ["amazon", "amz", "shopping", "buy"], "Web Search Bangs"),
        new("ai ", "ai explain recursion", "Ask ChatGPT or AI assistant", SymbolRegular.Sparkle24, ["ai", "chatgpt", "claude", "prompt"], "Web Search Bangs"),
        new("g ", "g fluent design", "Search Google on the web", SymbolRegular.Search24, ["google", "web search", "browse", "internet", "g"], "Web Search Bangs"),
        new("yt ", "yt lofi hip hop", "Search videos and music on YouTube", SymbolRegular.Play24, ["youtube", "video", "music", "stream", "yt"], "Web Search Bangs"),
        new("gh ", "gh metrohub", "Search code repositories on GitHub", SymbolRegular.Code24, ["github", "git", "repo", "repository", "code", "gh"], "Web Search Bangs"),
        new("ddg ", "ddg privacy mode", "Search privately with DuckDuckGo", SymbolRegular.GlobeSearch24, ["duckduckgo", "ddg", "private search"], "Web Search Bangs")
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
                _ = PowerService.ExecuteLockAsync();
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
                _ = PowerService.ExecuteSleepAsync();
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.WeatherMoon24),

        new(
            Id: "sys:hibernate",
            Title: "Hibernate",
            Description: "Save session to disk and power off computer",
            Keywords: ["hibernate", "hibernation", "deep sleep"],
            RequiresConfirm: true,
            ConfirmPrompt: "Press Enter again to hibernate",
            ExecuteAsync: ctx =>
            {
                if (!ctx.Confirmed)
                {
                    return Task.FromResult(new ActionResult(ActionOutcome.RequireConfirmation, "Press Enter again to hibernate"));
                }

                _ = PowerService.ExecuteHibernateAsync();
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Pause24),

        new(
            Id: "sys:restart",
            Title: "Restart",
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

                _ = PowerService.ExecuteRestartAsync();
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.ArrowCounterclockwise24),

        new(
            Id: "sys:shutdown",
            Title: "Shut down",
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

                _ = PowerService.ExecuteShutdownAsync();
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

                _ = PowerService.ExecuteSignOutAsync();
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.SignOut24),

        new(
            Id: "sys:recycle",
            Title: "Empty Recycle Bin",
            Description: "Permanently delete files in the Recycle Bin",
            Keywords: ["empty recycle bin", "recycle bin", "trash", "empty trash", "recycle", "bin", "trash bin", "rubbish"],
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
            Icon: SymbolRegular.Delete24),

        new(
            Id: "sys:flushdns",
            Title: "Flush DNS Cache",
            Description: "Clear and reset the Windows DNS client resolver cache (ipconfig /flushdns)",
            Keywords: ["flush dns", "flushdns", "ipconfig /flushdns", "dns cache", "clear dns", "dns"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ipconfig.exe",
                        Arguments = "/flushdns",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(3000);
                    return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, "DNS resolver cache successfully flushed"));
                }
                catch (Exception ex)
                {
                    Safe.Log("SystemCommands.FlushDns", ex);
                    return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, "Failed to flush DNS cache"));
                }
            },
            Icon: SymbolRegular.ArrowSync24),

        new(
            Id: "sys:restart_explorer",
            Title: "Restart Windows Explorer",
            Description: "Terminate and restart explorer.exe process to refresh desktop & taskbar",
            Keywords: ["restart explorer", "restart windows explorer", "explorer.exe", "restart taskbar", "reload explorer", "kill explorer"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName("explorer"))
                    {
                        try { p.Kill(); p.WaitForExit(1000); } catch { }
                        finally { p.Dispose(); }
                    }
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                    return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, "Windows Explorer restarted"));
                }
                catch (Exception ex)
                {
                    Safe.Log("SystemCommands.RestartExplorer", ex);
                    return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, "Failed to restart Windows Explorer"));
                }
            },
            Icon: SymbolRegular.ArrowCounterclockwise24),

        new(
            Id: "sys:copy_ipv4",
            Title: "Copy Local IPv4 Address",
            Description: "Detect active local network IP address and copy to clipboard",
            Keywords: ["copy ip", "ip address", "local ip", "ipv4", "my ip", "what is my ip"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                try
                {
                    string? ip = null;
                    var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                    foreach (var ni in interfaces)
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up ||
                            ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                            continue;

                        var ipProps = ni.GetIPProperties();
                        foreach (var addr in ipProps.UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                ip = addr.Address.ToString();
                                break;
                            }
                        }
                        if (ip != null) break;
                    }

                    if (!string.IsNullOrEmpty(ip))
                    {
                        ClipboardHelper.SetClipboardText(ip);
                        return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, $"IPv4 address copied: {ip}"));
                    }
                    return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, "No active local IPv4 address found"));
                }
                catch (Exception ex)
                {
                    Safe.Log("SystemCommands.CopyIp", ex);
                    return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, "Failed to retrieve local IPv4 address"));
                }
            },
            Icon: SymbolRegular.Wifi124),

        new(
            Id: "sys:open_hosts",
            Title: "Open Hosts File as Administrator",
            Description: "Open C:\\Windows\\System32\\drivers\\etc\\hosts in Notepad with admin privileges",
            Keywords: ["hosts", "hosts file", "edit hosts", "open hosts", "etc/hosts"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                try
                {
                    string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
                    ProcessLauncherService.LaunchTarget("notepad.exe", hostsPath, runAsAdmin: true);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                }
                catch (Exception ex)
                {
                    Safe.Log("SystemCommands.OpenHosts", ex);
                    return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, "Failed to open hosts file"));
                }
            },
            Icon: SymbolRegular.Document24),

        new(
            Id: "sys:toggle_hidden_files",
            Title: "Toggle Hidden Files & File Extensions",
            Description: "Show or hide hidden files, folders, and extensions in File Explorer",
            Keywords: ["hidden files", "show hidden files", "hide hidden files", "toggle hidden files", "file extensions", "show extensions"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: true);
                    if (key != null)
                    {
                        int currentHidden = (int)(key.GetValue("Hidden", 2) ?? 2);
                        int newHidden = currentHidden == 1 ? 2 : 1;
                        key.SetValue("Hidden", newHidden, RegistryValueKind.DWord);

                        int currentExt = (int)(key.GetValue("HideFileExt", 1) ?? 1);
                        int newExt = currentExt == 0 ? 1 : 0;
                        key.SetValue("HideFileExt", newExt, RegistryValueKind.DWord);

                        NativeMethodsSearch.SHChangeNotify(NativeMethodsSearch.SHCNE_ASSOCCHANGED, NativeMethodsSearch.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

                        string status = newHidden == 1 ? "Hidden files and extensions are now visible" : "Hidden files and extensions are now hidden";
                        return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, status));
                    }
                    return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, "Explorer settings registry key not found"));
                }
                catch (Exception ex)
                {
                    Safe.Log("SystemCommands.ToggleHiddenFiles", ex);
                    return Task.FromResult(new ActionResult(ActionOutcome.ShowMessage, "Failed to toggle hidden files"));
                }
            },
            Icon: SymbolRegular.EyeOff24),

        new(
            Id: "tool:ncpa",
            Title: "Network Connections (ncpa.cpl)",
            Description: "Manage network adapters, Ethernet, Wi-Fi, and IP settings",
            Keywords: ["ncpa.cpl", "network", "adapters", "ethernet", "wifi", "network connections", "ip address", "lan", "nic"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("ncpa.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Wifi124),

        new(
            Id: "tool:sysdm",
            Title: "System Properties & Environment Variables (sysdm.cpl)",
            Description: "Advanced system settings, environment variables, computer name",
            Keywords: ["sysdm.cpl", "system properties", "environment variables", "env", "path", "computer name", "advanced system settings"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("sysdm.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Settings24),

        new(
            Id: "tool:appwiz",
            Title: "Programs and Features (appwiz.cpl)",
            Description: "Classic Windows add or remove programs and uninstall panel",
            Keywords: ["appwiz.cpl", "programs and features", "uninstall", "add remove programs", "control panel", "installed apps"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("appwiz.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Apps24),

        new(
            Id: "tool:mmsys",
            Title: "Sound Control Panel (mmsys.cpl)",
            Description: "Classic playback, recording, and communication audio devices",
            Keywords: ["mmsys.cpl", "sound control panel", "audio devices", "playback", "recording", "microphone", "sound settings"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("mmsys.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Speaker224),

        new(
            Id: "tool:devmgmt",
            Title: "Device Manager (devmgmt.msc)",
            Description: "Manage hardware devices, display adapters, and device drivers",
            Keywords: ["devmgmt.msc", "device manager", "devices", "drivers", "hardware", "peripherals"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("devmgmt.msc");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Desktop24),

        new(
            Id: "tool:diskmgmt",
            Title: "Disk Management (diskmgmt.msc)",
            Description: "Create, format, and manage hard disk drives and partitions",
            Keywords: ["diskmgmt.msc", "disk management", "partitions", "format disk", "drives", "volumes", "ssd", "hdd"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("diskmgmt.msc");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Folder24),

        new(
            Id: "tool:services",
            Title: "Services (services.msc)",
            Description: "Start, stop, and configure Windows background services and daemons",
            Keywords: ["services.msc", "services", "windows services", "daemons", "background services"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("services.msc");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Settings24),

        new(
            Id: "tool:eventvwr",
            Title: "Event Viewer (eventvwr.msc)",
            Description: "View application errors, system event logs, and security audits",
            Keywords: ["eventvwr.msc", "event viewer", "logs", "error logs", "crash log", "system log", "diagnostics"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("eventvwr.msc");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Document24),

        new(
            Id: "tool:taskschd",
            Title: "Task Scheduler (taskschd.msc)",
            Description: "Schedule automated tasks, jobs, and batch scripts",
            Keywords: ["taskschd.msc", "task scheduler", "schedule", "cron", "automated tasks", "jobs"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("taskschd.msc");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Clock24),

        new(
            Id: "tool:resmon",
            Title: "Resource Monitor (resmon)",
            Description: "Detailed real-time CPU, Memory, Disk, and Network monitoring",
            Keywords: ["resmon", "resource monitor", "resmon.exe", "perf", "performance", "cpu ram disk network"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("resmon.exe");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Desktop24),

        new(
            Id: "tool:perfmon_rel",
            Title: "Reliability Monitor (perfmon /rel)",
            Description: "View stability history, application crashes, and Windows update records",
            Keywords: ["perfmon /rel", "reliability monitor", "reliability", "crash history", "stability index", "perfmon"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("perfmon.exe", "/rel");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.ArrowCounterclockwise24),

        new(
            Id: "tool:regedit",
            Title: "Registry Editor (regedit)",
            Description: "Browse and edit Windows Registry keys and values",
            Keywords: ["regedit", "regedit.exe", "registry", "registry editor", "hkey"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("regedit.exe");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Settings24),

        new(
            Id: "tool:cleanmgr",
            Title: "Disk Cleanup (cleanmgr)",
            Description: "Clean temporary files, recycle bins, and system caches",
            Keywords: ["cleanmgr", "cleanmgr.exe", "disk cleanup", "clean disk", "free space"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("cleanmgr.exe");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Delete24),

        new(
            Id: "tool:firewall",
            Title: "Windows Defender Firewall (firewall.cpl)",
            Description: "Manage incoming and outgoing network ports and firewall rules",
            Keywords: ["firewall.cpl", "firewall", "windows firewall", "ports", "inbound rules", "security"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("firewall.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Shield32),

        new(
            Id: "tool:powercfg",
            Title: "Power Options (powercfg.cpl)",
            Description: "Classic control panel for Windows power plans and sleep timing",
            Keywords: ["powercfg.cpl", "power options", "power plans", "high performance", "sleep settings"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("powercfg.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.BatteryCharge24),

        new(
            Id: "tool:msconfig",
            Title: "System Configuration (msconfig)",
            Description: "Configure Windows boot parameters, startup services, and tools",
            Keywords: ["msconfig", "msconfig.exe", "system configuration", "boot options", "startup services"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("msconfig.exe");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Settings24),

        new(
            Id: "tool:dxdiag",
            Title: "DirectX Diagnostic Tool (dxdiag)",
            Description: "Display detailed system hardware, GPU specifications, and DirectX drivers",
            Keywords: ["dxdiag", "dxdiag.exe", "directx", "gpu specs", "graphics card", "display drivers"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("dxdiag.exe");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Desktop24)
    ];
}
