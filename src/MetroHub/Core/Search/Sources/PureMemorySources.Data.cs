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
    SymbolRegular Icon = SymbolRegular.Power24,
    string KindLabel = "System");

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
        // Network & Connectivity
        new("Wi-Fi & Network", "ms-settings:network-wifi", "Manage Wi-Fi connections", ["wifi", "wi-fi", "wireless network", "internet connection"], SymbolRegular.Wifi124),
        new("Ethernet Network", "ms-settings:network-ethernet", "Wired network settings", ["ethernet", "lan", "wired network", "local area network"], SymbolRegular.Wifi124),
        new("Airplane Mode", "ms-settings:network-airplanemode", "Turn off wireless connections", ["airplane mode", "flight mode", "offline mode", "disable wireless"], SymbolRegular.Airplane24),
        new("Data Usage", "ms-settings:datausage", "Monitor internet data usage", ["data usage", "bandwidth", "internet data", "wifi data", "data limit"], SymbolRegular.DataUsage24),
        new("Proxy Settings", "ms-settings:network-proxy", "Network proxy configuration", ["proxy", "network proxy", "manual proxy", "pac script", "vpn proxy"], SymbolRegular.Globe24),

        // Bluetooth & Devices
        new("Bluetooth & devices", "ms-settings:bluetooth", "Manage wireless devices", ["bluetooth", "pair device", "wireless devices", "bluetooth devices"], SymbolRegular.Bluetooth24),
        new("Printers & Scanners", "ms-settings:printers", "Manage printers and scanners", ["printers", "scanners", "print", "add printer"], SymbolRegular.Document24),
        new("Mouse & Touchpad", "ms-settings:mousetouchpad", "Cursor speed and scrolling", ["mouse", "touchpad", "cursor", "pointer speed", "trackpad"], SymbolRegular.Desktop24),

        // Display & Visuals
        new("Display Settings", "ms-settings:display", "Brightness and resolution", ["display", "resolution", "scale", "monitor", "screen brightness"], SymbolRegular.Desktop24),
        new("Advanced Display", "ms-settings:display-advanced", "Refresh rate and display info", ["refresh rate", "refresh rates", "hz", "144hz", "60hz", "240hz", "120hz", "monitor hz", "advanced display", "fps"], SymbolRegular.Desktop24),
        new("Night Light", "ms-settings:nightlight", "Blue light filter", ["night light", "blue light", "warm screen", "reading mode", "eye comfort"], SymbolRegular.WeatherMoon24),

        // Sound & Audio
        new("Sound & Audio", "ms-settings:sound", "Speakers and microphone", ["sound", "audio", "volume", "speakers", "headphones"], SymbolRegular.Speaker224),
        new("Volume Mixer", "ms-settings:apps-volume", "Per-app volume levels", ["volume mixer", "mixer", "app volume", "sound mixer", "audio levels"], SymbolRegular.Speaker224),

        // Apps & Startup
        new("Startup Apps", "ms-settings:startupapps", "Manage startup apps", ["startup", "startup apps", "boot apps", "autostart", "start with windows"], SymbolRegular.Apps24),
        new("Installed Apps", "ms-settings:appsfeatures", "Uninstall or manage apps", ["installed apps", "uninstall", "programs", "applications", "remove apps"], SymbolRegular.Apps24),
        new("Default Apps", "ms-settings:defaultapps", "Choose default apps", ["default apps", "default browser", "file associations", "default programs"], SymbolRegular.Apps24),
        new("Optional Features", "ms-settings:optionalfeatures", "Extra Windows features", ["optional features", "windows features", "wsl", "hyper-v"], SymbolRegular.Settings24),

        // Personalization
        new("Background & Wallpaper", "ms-settings:personalization-background", "Change desktop wallpaper", ["background", "wallpaper", "desktop background", "desktop picture"], SymbolRegular.Image24),
        new("Colors", "ms-settings:personalization-colors", "Dark mode and colors", ["colors", "accent color", "dark mode", "light mode", "color theme"], SymbolRegular.Color24),
        new("Themes", "ms-settings:themes", "Desktop themes and sounds", ["themes", "desktop theme", "sound scheme", "cursors theme"], SymbolRegular.Color24),
        new("Lock Screen", "ms-settings:lockscreen", "Lock screen wallpaper", ["lock screen", "lockscreen", "screen saver", "lock picture"], SymbolRegular.LockClosed24),
        new("Taskbar & Navigation", "ms-settings:taskbar", "Customize the taskbar", ["taskbar", "taskbar icons", "dock alignment", "taskbar settings"], SymbolRegular.Desktop24),
        new("Start Menu", "ms-settings:personalization-start", "Customize Start menu layout", ["start menu", "start layout", "pinned apps", "start folders"], SymbolRegular.Apps24),

        // Gaming
        new("Game Mode", "ms-settings:gaming-gamemode", "Optimize PC for games", ["game mode", "gaming", "game optimization", "fps boost"], SymbolRegular.Games24),
        new("Xbox Game Bar", "ms-settings:gaming-gamebar", "Game overlay and clips", ["game bar", "xbox", "gamebar", "overlay", "win+g"], SymbolRegular.Games24),

        // Security & Privacy
        new("Sign-in Options", "ms-settings:signinoptions", "PIN, password, fingerprint", ["sign in", "signin", "pin", "password", "windows hello", "fingerprint", "login options"], SymbolRegular.Key24),
        new("Windows Security", "ms-settings:windowsdefender", "Antivirus and protection", ["windows security", "defender", "antivirus", "firewall protection"], SymbolRegular.Shield32),
        new("Privacy & Security", "ms-settings:privacy", "App permissions and privacy", ["privacy", "security permissions", "app permissions", "camera mic"], SymbolRegular.Shield32),
        new("Microphone Permissions", "ms-settings:privacy-microphone", "Microphone app permissions", ["microphone", "mic permission", "microphone permissions", "mic access", "voice privacy"], SymbolRegular.Mic24),

        // System & Performance
        new("Power & Battery", "ms-settings:powersleep", "Sleep and battery settings", ["power", "sleep timer", "battery saver", "energy mode"], SymbolRegular.BatteryCharge24),
        new("Storage & Disks", "ms-settings:storagesense", "Check free disk space", ["storage", "disk space", "free space", "drive cleanup", "clean disk"], SymbolRegular.Folder24),
        new("Notifications & Focus", "ms-settings:notifications", "Manage alerts and focus", ["notifications", "focus assist", "alerts", "do not disturb"], SymbolRegular.DismissCircle24),
        new("Windows Update", "ms-settings:windowsupdate", "Check for updates", ["windows update", "updates", "patch", "check updates"], SymbolRegular.Settings24),
        new("Delivery Optimization", "ms-settings:delivery-optimization", "Update download limits", ["delivery optimization", "bandwidth limits", "download speed", "update bandwidth"], SymbolRegular.ArrowDownload24),
        new("Activation", "ms-settings:activation", "Windows license status", ["activation", "windows license", "product key", "windows key"], SymbolRegular.CheckmarkCircle24),
        new("About System", "ms-settings:about", "PC specs and Windows info", ["about", "pc specs", "system specs", "device specifications", "ram processor"], SymbolRegular.Info24),
        new("Developer Settings", "ms-settings:developers", "Developer tools and options", ["developer settings", "developer mode", "sideload", "remote desktop dev"], SymbolRegular.Code24),

        // Time, Language & Accessibility
        new("Date & Time", "ms-settings:dateandtime", "Time zone and clock", ["date", "time", "clock", "timezone", "sync clock"], SymbolRegular.Clock24),
        new("Language & Region", "ms-settings:regionlanguage", "Display language and region", ["language", "region", "keyboard layout", "country", "system language"], SymbolRegular.Globe24),
        new("Live Captions", "ms-settings:easeofaccess-closedcaptioning", "Automatic audio subtitles", ["live captions", "captions", "subtitles", "closed captions", "speech to text"], SymbolRegular.ClosedCaption24)
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
        new("set ", "set bluetooth", "Open Windows settings", SymbolRegular.Settings24, ["settings", "windows settings", "preferences", "config", "control", "set"], "Windows & System Tools"),
        new("cp ", "cp mouse", "Open classic Control Panel applets", SymbolRegular.Settings24, ["control panel", "cpl", "classic", "cp", "legacy"], "Windows & System Tools"),
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
            Description: "Lock your PC",
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
            Description: "Put PC to sleep",
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
            Description: "Save session and power off",
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
            Description: "Restart your PC",
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
            Description: "Turn off your PC",
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
            Description: "Sign out of Windows",
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
            Description: "Empty the trash",
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
            Description: "Reset network cache",
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
            Description: "Refresh desktop and taskbar",
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
            Description: "Copy local IP address",
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
            Description: "Edit hosts file",
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
            Description: "Show or hide hidden files",
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
            Description: "Manage network adapters",
            Keywords: ["ncpa.cpl", "network", "adapters", "ethernet", "wifi", "network connections", "ip address", "lan", "nic"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("ncpa.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Wifi124,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:sysdm",
            Title: "System Properties & Environment Variables (sysdm.cpl)",
            Description: "Advanced system settings",
            Keywords: ["sysdm.cpl", "system properties", "environment variables", "env", "path", "computer name", "advanced system settings"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("sysdm.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Settings24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:appwiz",
            Title: "Programs and Features (appwiz.cpl)",
            Description: "Uninstall desktop programs",
            Keywords: ["appwiz.cpl", "programs and features", "uninstall", "add remove programs", "control panel", "installed apps"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("appwiz.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Apps24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:mmsys",
            Title: "Sound Control Panel (mmsys.cpl)",
            Description: "Classic audio settings",
            Keywords: ["mmsys.cpl", "sound control panel", "audio devices", "playback", "recording", "microphone", "sound settings"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("mmsys.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Speaker224,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:devmgmt",
            Title: "Device Manager (devmgmt.msc)",
            Description: "Hardware and drivers",
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
            Description: "Manage drives and partitions",
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
            Description: "Manage background tasks",
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
            Description: "System logs and errors",
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
            Description: "Automate tasks and timers",
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
            Description: "Live CPU and RAM usage",
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
            Description: "System crash history",
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
            Description: "Advanced system registry",
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
            Description: "Delete temporary files",
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
            Description: "App internet access",
            Keywords: ["firewall.cpl", "firewall", "windows firewall", "ports", "inbound rules", "security"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("firewall.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Shield32,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:powercfg",
            Title: "Power Options (powercfg.cpl)",
            Description: "Manage power plans",
            Keywords: ["powercfg.cpl", "power options", "power plans", "high performance", "sleep settings"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("powercfg.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.BatteryCharge24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:msconfig",
            Title: "System Configuration (msconfig)",
            Description: "Startup and boot options",
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
            Description: "Graphics and audio specs",
            Keywords: ["dxdiag", "dxdiag.exe", "directx", "gpu specs", "graphics card", "display drivers"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("dxdiag.exe");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Desktop24),

        // Classic Control Panel Applets (.cpl & control.exe tools)
        new(
            Id: "tool:control",
            Title: "Control Panel",
            Description: "Classic Windows Control Panel",
            Keywords: ["control panel", "classic control panel", "settings classic", "cpl"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("control.exe");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Settings24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:mouse",
            Title: "Mouse Properties (Classic)",
            Description: "Pointer precision and speed",
            Keywords: ["main.cpl", "mouse properties", "pointer speed", "mouse sensitivity", "pointer precision", "pointer trails", "mouse buttons"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("main.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Desktop24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:keyboard",
            Title: "Keyboard Properties (Classic)",
            Description: "Repeat rate and delay",
            Keywords: ["keyboard properties", "repeat rate", "repeat delay", "cursor blink", "keyboard speed"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("control.exe", "keyboard");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Keyboard24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:joy",
            Title: "Game Controllers (Classic)",
            Description: "Calibrate gamepads and sticks",
            Keywords: ["joy.cpl", "game controllers", "controller drift", "joystick calibration", "gamepad test", "usb controller"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("joy.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Games24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:inetcpl",
            Title: "Internet Options",
            Description: "Proxy, TLS, and certificates",
            Keywords: ["inetcpl.cpl", "internet options", "certificates", "tls", "lan settings", "ssl proxy", "trusted sites"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("inetcpl.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Globe24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:intl",
            Title: "Region (Classic)",
            Description: "Formats and language admin",
            Keywords: ["intl.cpl", "region classic", "number formats", "non-unicode", "date format classic", "system locale"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("intl.cpl");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Globe24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:folders",
            Title: "File Explorer Options",
            Description: "Folder view and options",
            Keywords: ["folder options", "file explorer options", "show hidden folders", "file extensions", "folder view"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("control.exe", "folders");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Folder24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:srchadmin",
            Title: "Indexing Options",
            Description: "Windows search indexing",
            Keywords: ["indexing options", "rebuild index", "search indexing", "index locations", "search speed"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("control.exe", "srchadmin.dll");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Search24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:credmgr",
            Title: "Credential Manager",
            Description: "Saved passwords and credentials",
            Keywords: ["credential manager", "saved passwords", "git credentials", "network logins", "web credentials", "windows credentials"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("control.exe", "keymgr.dll");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Key24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:netplwiz",
            Title: "User Accounts (Netplwiz)",
            Description: "Advanced user accounts",
            Keywords: ["netplwiz", "netplwiz.exe", "auto login", "user accounts classic", "user groups", "passwords2"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("netplwiz.exe");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Person24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:colorcpl",
            Title: "Color Management",
            Description: "Display color profiles (ICC)",
            Keywords: ["colorcpl", "colorcpl.exe", "color management", "icc profile", "monitor calibration", "display profile", "color calibration"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("colorcpl.exe");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Color24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:uac",
            Title: "UAC Settings",
            Description: "User Account Control slider",
            Keywords: ["uac", "user account control", "admin popup", "uac slider", "security prompt", "uac settings"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("UserAccountControlSettings.exe");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Shield24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:autoplay",
            Title: "AutoPlay",
            Description: "USB and drive defaults",
            Keywords: ["autoplay", "usb defaults", "removable drive actions", "memory card autoplay"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("control.exe", "/name Microsoft.AutoPlay");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Play24,
            KindLabel: "Control Panel"),

        new(
            Id: "tool:printers",
            Title: "Devices and Printers",
            Description: "Classic printers and devices",
            Keywords: ["devices and printers", "printers classic", "print queue", "printer properties"],
            RequiresConfirm: false,
            ConfirmPrompt: null,
            ExecuteAsync: _ =>
            {
                ProcessLauncherService.LaunchTarget("control.exe", "printers");
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            },
            Icon: SymbolRegular.Document24,
            KindLabel: "Control Panel")
    ];
}
