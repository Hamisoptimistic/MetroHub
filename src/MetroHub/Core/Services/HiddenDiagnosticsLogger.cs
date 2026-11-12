using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Serilog;

namespace MetroHub.Core.Services;

/// <summary>
/// Diagnostic logger that captures system, memory, and handle telemetry.
/// Emits non-blocking structured events via Serilog into the rolling log file and Seq,
/// eliminating synchronous disk I/O and locks from the UI thread.
/// </summary>
public static class HiddenDiagnosticsLogger
{
    private static readonly ILogger Logger = Serilog.Log.ForContext("Source", "Diagnostics");
    private static string _logFilePath = AppPaths.HiddenDiagnosticsLogPath;
    private static readonly Lock _lock = new();

    public static bool IsEnabled { get; set; }

    static HiddenDiagnosticsLogger()
    {
        try
        {
            AppPaths.EnsureDirectory(_logFilePath);
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "HiddenDiagnosticsLogger.cctor.AppPaths");
            try
            {
                _logFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "hidden_diagnostics.log");
                string? targetDir = System.IO.Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(targetDir) && !System.IO.Directory.Exists(targetDir))
                {
                    System.IO.Directory.CreateDirectory(targetDir);
                }
            }
            catch (Exception innerEx)
            {
                Safe.Ignore(innerEx, "HiddenDiagnosticsLogger.cctor.BaseDirectory");
            }
        }
    }

    public static void LogTransition(bool isVisible)
    {
        if (!IsEnabled) return;

        try
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();

            double wsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            double privMb = proc.PrivateMemorySize64 / (1024.0 * 1024.0);
            double heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            uint gdi = NativeMethods.GetGuiResources(proc.Handle, NativeMethods.GR_GDIOBJECTS);
            uint user = NativeMethods.GetGuiResources(proc.Handle, NativeMethods.GR_USEROBJECTS);
            int handles = proc.HandleCount;

            string state = isVisible ? "SHOWN (Foreground Active)" : "HIDDEN (Background Dormant)";
            Logger.Information("[TRANSITION] MetroHub is now {State} | WS: {WorkingSetMb:0.0} MB | Private: {PrivateBytesMb:0.0} MB | Managed: {ManagedMb:0.0} MB | GDI/USER: {GdiCount}/{UserCount} | Handles: {HandleCount}",
                state, wsMb, privMb, heapMb, gdi, user, handles);
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "HiddenDiagnosticsLogger.LogTransition");
        }
    }

    public static void Log(string message)
    {
        if (!IsEnabled) return;
        WriteEntry(message);
    }

    public static void LogHiddenEvent(string source, string eventName, string? details = null)
    {
        if (!IsEnabled) return;
        if (!HubState.IsHidden) return;

        double wsMb = GetWorkingSetMb();
        string detailStr = string.IsNullOrWhiteSpace(details) ? string.Empty : $" | Details: {details}";
        Logger.Information("[HIDDEN ACTIVITY DETECTED] [{EventSource}] {EventName} | Working Set: {WorkingSetMb:0.0} MB{Details}",
            source, eventName, wsMb, detailStr);
    }

    /// <summary>
    /// Captures and logs a deep, multi-tier memory diagnostic report covering:
    /// 1. OS & Process physical/virtual memory (Working Set, Peak, Private Bytes, Paged, Virtual)
    /// 2. Windows GUI & Kernel handles (GDI, USER, Kernel handles, Thread count)
    /// 3. .NET CLR GC memory (Managed heap, Gen 0/1/2, LOH, POH, fragmentation, collection counts)
    /// 4. WPF UI state (Active windows)
    /// </summary>
    public static void LogMemorySnapshot(string trigger, double? beforeManagedMb = null)
    {
        if (!IsEnabled) return;

        try
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();

            // 1. Process OS Memory
            double wsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            double peakWsMb = proc.PeakWorkingSet64 / (1024.0 * 1024.0);
            double privMb = proc.PrivateMemorySize64 / (1024.0 * 1024.0);
            double pagedMb = proc.PagedMemorySize64 / (1024.0 * 1024.0);
            double peakPagedMb = proc.PeakPagedMemorySize64 / (1024.0 * 1024.0);
            double virtMb = proc.VirtualMemorySize64 / (1024.0 * 1024.0);
            int handles = proc.HandleCount;
            int threads = proc.Threads.Count;

            // 2. Win32 GUI Handles
            uint gdiCount = NativeMethods.GetGuiResources(proc.Handle, NativeMethods.GR_GDIOBJECTS);
            uint userCount = NativeMethods.GetGuiResources(proc.Handle, NativeMethods.GR_USEROBJECTS);

            // 3. CLR Managed Memory
            double currentManagedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            int gen0Collections = GC.CollectionCount(0);
            int gen1Collections = GC.CollectionCount(1);
            int gen2Collections = GC.CollectionCount(2);

            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            double heapSizeMb = gcInfo.HeapSizeBytes / (1024.0 * 1024.0);
            double fragmentedMb = gcInfo.FragmentedBytes / (1024.0 * 1024.0);
            double fragPercent = heapSizeMb > 0 ? (fragmentedMb / heapSizeMb) * 100.0 : 0;

            double gen0Mb = 0, gen1Mb = 0, gen2Mb = 0, lohMb = 0, pohMb = 0;
            var genInfo = gcInfo.GenerationInfo;
            if (genInfo.Length > 0) gen0Mb = genInfo[0].SizeAfterBytes / (1024.0 * 1024.0);
            if (genInfo.Length > 1) gen1Mb = genInfo[1].SizeAfterBytes / (1024.0 * 1024.0);
            if (genInfo.Length > 2) gen2Mb = genInfo[2].SizeAfterBytes / (1024.0 * 1024.0);
            if (genInfo.Length > 3) lohMb = genInfo[3].SizeAfterBytes / (1024.0 * 1024.0);
            if (genInfo.Length > 4) pohMb = genInfo[4].SizeAfterBytes / (1024.0 * 1024.0);

            // 4. WPF State
            int windowCount = System.Windows.Application.Current?.Windows.Count ?? 0;

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"==================== [MEMORY DIAGNOSTIC SNAPSHOT: {trigger}] ====================");
            sb.AppendLine("  [OS & Process Memory]");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Working Set (Physical RAM):  {wsMb:0.0} MB  (Peak: {peakWsMb:0.0} MB)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Private Bytes (True Commit): {privMb:0.0} MB  <-- [Crucial: tracks real unmanaged leaks]");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Paged Memory (Commit Size):  {pagedMb:0.0} MB  (Peak: {peakPagedMb:0.0} MB)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Virtual Address Space:       {virtMb:0.0} MB");
            sb.AppendLine($"  [Handles & Win32 Resources]");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    GDI Objects:                 {gdiCount} / 10,000  (Bitmaps, Pens, Brushes, DCs)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    USER Objects:                {userCount} / 10,000  (Windows, Menus, Hooks, Timers)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Kernel Handles:              {handles}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Active Threads:              {threads}");
            sb.AppendLine($"  [.NET Managed Heap (CLR GC)]");
            if (beforeManagedMb.HasValue)
            {
                double freed = beforeManagedMb.Value - currentManagedMb;
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Managed Heap:                {beforeManagedMb.Value:0.0} MB → {currentManagedMb:0.0} MB (Freed: {freed:0.0} MB)");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Live Allocated Objects:      {currentManagedMb:0.0} MB");
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Total Heap Size:             {heapSizeMb:0.0} MB");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Heap Fragmentation:          {fragmentedMb:0.0} MB ({fragPercent:0.0}%)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Gen 0: {gen0Mb:0.0} MB  (Collections: {gen0Collections})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Gen 1: {gen1Mb:0.0} MB  (Collections: {gen1Collections})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Gen 2: {gen2Mb:0.0} MB  (Collections: {gen2Collections})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    LOH (Large Object Heap):     {lohMb:0.0} MB");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    POH (Pinned Object Heap):    {pohMb:0.0} MB");
            sb.AppendLine("  [WPF Runtime]");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Loaded Windows Count:        {windowCount}");
            sb.AppendLine("==================================================================================");

            Logger.Information(
                "[SNAPSHOT] {Trigger} | WS: {WorkingSetMb:0.0} MB | Priv: {PrivateBytesMb:0.0} MB | Heap: {HeapSizeMb:0.0} MB | GDI/USER: {GdiCount}/{UserCount} | Handles: {HandleCount}\n{SnapshotText}",
                trigger, wsMb, privMb, heapSizeMb, gdiCount, userCount, handles, sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "HiddenDiagnosticsLogger.CaptureSnapshot");
        }
    }

    private static double GetWorkingSetMb()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            return proc.WorkingSet64 / (1024.0 * 1024.0);
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "HiddenDiagnosticsLogger.GetWorkingSetMb");
            return -1;
        }
    }

    private static void WriteEntry(string line)
    {
        try
        {
            Logger.Information("{Message}", line);

            string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}";
            lock (_lock)
            {
                System.IO.File.AppendAllText(_logFilePath, entry);
            }
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "HiddenDiagnosticsLogger.WriteEntry");
        }
    }
}
