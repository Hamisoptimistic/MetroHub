# MetroHub Widget System — Architecture Blueprint

## TL;DR verdict
Yes, this is achievable under ~60MB with widgets active, *if* you follow one rule religiously:

> **A widget that isn't open does no work. Zero timers, zero polling, zero COM objects held open.**

Every RAM disaster in this category (and there are many — search "Rainmeter high memory" or any Electron-based widget board) comes from background polling that runs whether or not anyone's looking. You already said refresh should be **on-demand-only-when-opened**, which is exactly the right call — lean into it hard, including tearing widgets down (not just pausing) when closed.

Reference points that prove this is real:
- **EarTrumpet** (WPF, per-app volume mixer) ships at a few tens of MB idle using raw CoreAudio COM interop, not polling.
- **LibreHardwareMonitor**-based tools (SysMonAvalonia, Fluent Sensors/WinUI3, TrafficMonitor) do CPU/GPU/RAM/storage sensors in small desktop apps.
- Windows 11's own Widgets board is a WebView2 app and is *much* heavier than what you're building — you have a real advantage going native WPF.

The one honest caveat: an **empty** `dotnet new wpf` project can show 100–260MB in Task Manager in some measurements. That number is almost always from `dotnet run` (dev hosting) or Debug config, not a `dotnet publish -c Release` self-contained/ReadyToRun build. Framework-dependent Release builds of real WPF apps commonly idle in the 40–90MB range; your existing MetroHub (WPF-UI + a tile grid) is already proof this isn't fantasy. Budget realistically: **~50–70MB base app + 5–15MB per open widget**, not "under 60MB no matter what's open." If you truly need every widget open simultaneously under 60MB total, that's tight but reachable with the lazy-teardown pattern below — just don't treat it as free.

---

## 1. Don't reinvent widget chrome — WPF-UI already has it

Upgrade `WPF-UI` 3.0.4 → **4.0.2** (net9 support, signed packages, `Wpf.Ui.Abstractions` split out). For the widget *shell* (the card look in your Windows-11-widgets-board screenshot), you don't need custom XAML from scratch:

- `Wpf.Ui.Controls.CardControl` / `CardExpander` — gives you the rounded-corner acrylic card for free.
- `Wpf.Ui.Controls.FluentWindow` + Mica/Acrylic backdrop (`WindowBackdropType`) if a widget ever needs to float as its own window.
- Fluent System Icons are bundled — no separate icon font needed for network/sound/battery-style glyphs.

So: **don't hardcode every widget's visual chrome.** Build one `WidgetCard` control (a thin wrapper around `ui:CardExpander` with a drag handle, a title, and a `ContentPresenter`), and every widget just supplies its *inner* content via a DataTemplate. This is the actual answer to "do we have to hardcode every UI" — no, you hardcode the shell once and template the guts.

---

## 2. The core abstraction: widgets as data, not code-behind

Given your codebase has no MVVM toolkit yet, this is the moment to introduce one. Use **CommunityToolkit.Mvvm** (source-generator based, near-zero runtime cost, `[ObservableProperty]`/`[RelayCommand]`) — it's the de facto standard, actively maintained by Microsoft, and won't add meaningful RAM.

```csharp
// Core/Widgets/IWidget.cs
public interface IWidget
{
    string Id { get; }              // "sound", "network", "taskmgr", "storage"
    string DisplayName { get; }
    IconSource Icon { get; }
    bool IsMovable => true;
    bool IsResizable => false;      // per your answer to Q6

    /// Called when the widget becomes visible on the canvas.
    /// This is where timers/COM objects/handles get created.
    Task ActivateAsync(CancellationToken ct);

    /// Called when the widget is closed/scrolled off/app minimized to tray.
    /// Must fully release native resources — not just pause.
    void Deactivate();
}

// Every concrete widget is a ViewModel implementing this,
// paired with a DataTemplate keyed by widget type.
public abstract partial class WidgetViewModelBase : ObservableObject, IWidget
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract IconSource Icon { get; }
    public abstract Task ActivateAsync(CancellationToken ct);
    public abstract void Deactivate();
}
```

```xml
<!-- App.xaml or a WidgetTemplates.xaml resource dictionary -->
<DataTemplate DataType="{x:Type vm:SoundWidgetViewModel}">
    <views:SoundWidgetView />
</DataTemplate>
<DataTemplate DataType="{x:Type vm:NetworkWidgetViewModel}">
    <views:NetworkWidgetView />
</DataTemplate>
<DataTemplate DataType="{x:Type vm:TaskManagerWidgetViewModel}">
    <views:TaskManagerWidgetView />
</DataTemplate>
```

Then your canvas is just an `ItemsControl`/`Canvas` bound to `ObservableCollection<WidgetViewModelBase>` with `WrapPanel`-style positioning (reuse the drag logic you already have via `gong-wpf-dragdrop`, which is already in your `.csproj` — no need for a second drag library). WPF's implicit `DataTemplate` selection means **adding a new widget type is: one ViewModel class + one View + one DataTemplate entry.** No central switch statement, no hardcoded per-widget canvas logic.

**Widget host lifecycle** (this is the RAM-critical part):

```csharp
public sealed class WidgetHostViewModel : ObservableObject
{
    public ObservableCollection<WidgetViewModelBase> ActiveWidgets { get; } = new();

    public async Task AddWidgetAsync(WidgetViewModelBase widget)
    {
        ActiveWidgets.Add(widget);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await widget.ActivateAsync(cts.Token); // starts its timer/COM here
    }

    public void RemoveWidget(WidgetViewModelBase widget)
    {
        widget.Deactivate();       // stop timer, release COM, dispose PDH query
        ActiveWidgets.Remove(widget); // drop the ViewModel entirely — let GC collect
    }

    // Call this from MainWindow's hide/minimize-to-tray path
    public void DeactivateAll()
    {
        foreach (var w in ActiveWidgets) w.Deactivate();
    }

    public async Task ReactivateAllAsync()
    {
        foreach (var w in ActiveWidgets) await w.ActivateAsync(CancellationToken.None);
    }
}
```

Since MetroHub already hides-to-tray instead of closing, hook `DeactivateAll()`/`ReactivateAllAsync()` into your existing `ShowScreen()`/`ToggleVisibility()` calls in `MainWindow.xaml.cs`. This alone is what gets you toward "under 60MB" — a widget sitting on the canvas while the whole app is hidden in the tray should cost you **nothing** beyond its static UI tree.

No DI container is strictly required for this scale (maybe a dozen widget types) — a simple `Dictionary<string, Func<WidgetViewModelBase>>` widget factory registered in `App.xaml.cs` is enough and avoids adding `Microsoft.Extensions.DependencyInjection` weight for no real benefit. Add DI later only if this grows into plugin territory.

---

## 3. Per-widget implementation notes

### 3a. Sound widget — master volume + device select (EarTrumpet-lite)
You explicitly don't need per-app mixing, so skip EarTrumpet's `IAudioSessionManager2` complexity entirely. Use **`AudioSwitcher.AudioApi.CoreAudio`** (NuGet, MIT, actively used — wraps `IMMDeviceEnumerator`/`IAudioEndpointVolume`/the undocumented `IPolicyConfig` for default-device switching):

```csharp
var controller = new CoreAudioController();
var playbackDevices = controller.GetPlaybackDevices(DeviceState.Active);
var current = playbackDevices.FirstOrDefault(d => d.IsDefaultDevice);
current.Volume = 65; // 0-100
await current.SetAsDefaultAsync(); // switch speaker/earphone/etc.
```
Subscribe to `controller.AudioDeviceChanged` for live updates instead of polling — **event-driven, zero timer needed.** Dispose the `CoreAudioController` in `Deactivate()`.

### 3b. Network widget — icons now, graphs later (you asked about both)
**Icons (status only, your v1 scope):** Wi-Fi/Bluetooth/Airplane-mode state are Windows Runtime APIs, not classic .NET — you'll need `Microsoft.Windows.SDK.Contracts` (CsWinRT) to call:
- `Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()` for connection type/signal bars.
- `Windows.Devices.Radios.Radio.GetRadiosAsync()` for Wi-Fi/Bluetooth/Airplane-mode toggle state (and to actually toggle them).

These are event-capable (`NetworkInformation.NetworkStatusChanged`) — again no polling needed for icon state.

**Graphs (deferred, but here's the shape when you get there):** `System.Net.NetworkInformation.NetworkInterface.GetIPv4Statistics()` polled at your widget's visible-refresh interval gives bytes sent/received deltas — this is cheap (no admin, no WMI) and is genuinely fine to poll at 1–2s *only while the widget is open*, since it's a plain managed API call, not a COM/WMI round-trip.

### 3c. Task Manager–style widget — the one that separates toy demos from real tools
Per-process **CPU%** and **RAM**: use `System.Diagnostics.Process` (`WorkingSet64`, and `TotalProcessorTime` delta over your poll interval) — cheap, no admin required.

Per-process **Disk I/O**: `GetProcessIoCounters` via P/Invoke (`ReadTransferCount`/`WriteTransferCount`, delta-based) — also no admin required.

Per-process **GPU%**: this is the one genuinely awkward metric. Task Manager itself gets it from **PDH "GPU Engine" performance counters** (`\GPU Engine(pid_1234_luid_..._phys_0_eng_0_engtype_3D)\Utilization Percentage`), which is undocumented-but-stable and is what every third-party tool (including Task Manager) actually reads. Use the native PDH API (`PdhOpenQuery`/`PdhAddCounter`/`PdhCollectQueryData`) directly via P/Invoke rather than `System.Diagnostics.PerformanceCounter` — the managed `PerformanceCounter` class has real per-call overhead and GC pressure from marshaling; raw PDH is what low-footprint tools use. Instance names must be enumerated with wildcards (`PdhExpandWildCardPath`) since they include PID+LUID+engine-type per process — expect to write a small wrapper, this is the fiddliest part of the whole project.

**Do not use WMI (`Win32_PerfFormattedData_*`) for a widget that polls every second** — WMI queries have measurable per-call overhead (COM marshaling, a few ms minimum) that adds up fast at 1s intervals across many processes. Raw PDH counters are what Task Manager itself uses under the hood and are dramatically cheaper.

**Top-N only, refresh on open, pause on scroll-away** — you already scoped this correctly (simple app-name + CPU/RAM/Disk/GPU row, not a full Task Manager clone with per-thread/handle data). That scope is very achievable at low cost.

### 3d. Storage widget — SMART health, and why it's messier than it looks
You picked `MSStorageDriver_FailurePredictStatus` — works fine for **SATA/IDE drives**, but it's known to return nothing or fail outright on **NVMe** drives (this shows up constantly in forum threads — BigFix, TenForums, Microsoft Q&A all report the same gap). NVMe SMART/health data instead requires `DeviceIoControl` with `IOCTL_STORAGE_QUERY_PROPERTY` (`StorageDeviceProtocolSpecificProperty`, `ProtocolTypeNvme`) to pull the NVMe Health Log Page — hand-rolling that P/Invoke + parsing the log page struct is genuinely fiddly (this is exactly the pain point people hit trying to port smartmontools-style tools to C#/VB, per Microsoft Q&A threads on this).

**Recommendation: don't hand-roll this one.** Use **`LibreHardwareMonitorLib`** (NuGet, MPL-2.0, actively maintained, targets `net8.0`/`net9.0`/`net10.0` directly) — it already handles SATA SMART *and* NVMe health logs *and* GPU sensors *and* per-core CPU, across Intel/AMD/NVIDIA, and is the engine behind several real Fluent-styled Windows 11 monitor apps (Fluent Sensors, SysMonAvalonia). This single dependency could actually replace your hand-rolled GPU-PDH work too, at the cost of some sensors needing elevation:

```csharp
var computer = new Computer { IsStorageEnabled = true, IsGpuEnabled = true };
computer.Open();
computer.Accept(new UpdateVisitor());
// iterate computer.Hardware -> HardwareType.Storage -> Sensors (Wear %, Temperature, etc.)
computer.Close(); // release everything — call this in Deactivate()
```

Trade-off to weigh: LHM's *deep* sensor access (voltages, per-core temps) loads a kernel driver (WinRing0-derived) and needs admin — but **basic drive health / SMART / disk temp does not require the driver path** in most cases; only motherboard/voltage-level sensors do. Keep `IsMotherboardEnabled = false` and you avoid most of the elevation requirement. Since you only need this widget on-demand, `computer.Open()` in `ActivateAsync` and `computer.Close()` in `Deactivate()` means its footprint only exists while the widget is on screen — consistent with your on-demand refresh answer.

### 3e. Weather widget
No native Windows API gives you forecast data (it's not on-device). You'll hit a public HTTP API (Open-Meteo is free/no-key and commonly used for exactly this kind of widget; OpenWeatherMap if you want more detail). `HttpClient` should be a single static/reused instance app-wide (never `new HttpClient()` per widget instance — that's a classic .NET socket-exhaustion and memory footgun, worth flagging since it's an easy mistake at this scale) and only fetched on widget open + a coarse cache (weather doesn't need to refresh every second — 10–15 min is plenty, and only while the widget is visible).

### 3f. Calculator / Notes as embedded mini-apps
You want these embedded, not launched. Both are legitimately easy in WPF with no external dependency:
- **Calculator**: a `TextBox` + button grid + a simple recursive-descent expression evaluator (don't pull in a scripting engine like Roslyn/NCalc for this — massive overkill in both complexity and RAM for basic arithmetic; ~150 lines of hand-written parsing is more than enough and costs nothing at rest).
- **Notes**: a `TextBox`/`RichTextBox` bound to a ViewModel that persists to disk via the `StorageService` you already have — this is the cheapest widget in the whole set, effectively free.

---

## 4. Other widget ideas worth considering (you asked)
- **Battery/power widget** (laptops) — `System.Windows.Forms.SystemInformation.PowerStatus` or `Windows.System.Power.PowerManager` (WinRT) for charge %, time remaining, power plan — cheap, event-driven via `PowerManager.EnergySaverStatusChanged` etc.
- **Clipboard history mini-widget** — `Clipboard`/`AddClipboardFormatListener` (P/Invoke), event-driven, no polling at all.
- **Quick-launch/pinned-folder widget** — reuses your existing `AppScannerService`/`InstalledAppsService`, near-zero new code.
- **System uptime / quick shutdown-restart-sleep widget** — trivial, near-zero cost.
- **Screenshot/snip launcher tile** — just shells `ms-screenclip:` URI, no custom logic needed.

These are all "cheap to add, genuinely useful" — better ROI than something like a stock ticker or RSS widget, which pull you into background-polling-over-network territory again.

---

## 5. NuGet package summary

| Concern | Package | Why |
|---|---|---|
| Widget shell / theme | `WPF-UI` (upgrade to 4.0.2) | Already in your project; CardExpander/Mica/Acrylic native |
| MVVM | `CommunityToolkit.Mvvm` | Source-generated, near-zero runtime cost, Microsoft-maintained |
| Master volume + device switch | `AudioSwitcher.AudioApi.CoreAudio` | Exactly your scope (no per-app mixing needed) |
| Wi-Fi/BT/Airplane state | `Microsoft.Windows.SDK.Contracts` (CsWinRT) | WinRT Radios/NetworkInformation APIs |
| CPU/RAM/Disk-IO per process | none — `System.Diagnostics.Process` + P/Invoke `GetProcessIoCounters` | Built-in, cheapest option |
| GPU per process | none — raw PDH via P/Invoke | Avoid managed `PerformanceCounter` overhead |
| Storage SMART (incl. NVMe) + GPU sensors alt. | `LibreHardwareMonitorLib` | Handles the NVMe IOCTL mess you'd otherwise hand-roll |
| Drag/reposition | `gong-wpf-dragdrop` (already present) | Don't add a second drag library |
| Weather | none — `HttpClient` (single shared instance) + Open-Meteo | No key required |

---

## 6. The realistic RAM budget, stated plainly
- **Base app (idle, no widgets open):** ~50–70MB is a fair Release-build target given WPF-UI + your tile system. Verify with a self-contained `Release` publish, not `dotnet run`/Debug — the scary 100–260MB numbers people report online are almost always measuring the wrong build config.
- **Per open widget:** Sound/Notes/Calculator/Clipboard/Battery ≈ negligible (a few hundred KB–1-2MB each, mostly UI tree). Network icons ≈ small. Task-manager widget ≈ a few MB while its PDH query + Process snapshot is live. Storage/GPU-via-LibreHardwareMonitor ≈ the heaviest single widget (library init cost), but only while open, and closed = fully released via `computer.Close()`.
- **The failure mode to avoid:** leaving *any* `DispatcherTimer`, PDH query, or `Computer` instance alive after a widget is closed or the app is tray-hidden. That's the only way this creeps past budget — not WPF, not WPF-UI, not the packages above.

Nobody's shipped this *exact* combination as one polished open-source app that I found, but every individual piece has working prior art (EarTrumpet for audio, LibreHardwareMonitor-based apps for sensors, PDH-based tools for GPU-per-process, Windows Widgets board for the card-based layout paradigm) — you're integrating known-solved problems, not inventing new ones. The real risk isn't feasibility, it's discipline about the activate/deactivate lifecycle as you add widget #7, #8, #9 and the temptation to just leave a timer running "for simplicity."
