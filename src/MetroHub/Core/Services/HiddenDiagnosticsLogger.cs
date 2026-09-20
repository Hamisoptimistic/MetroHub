using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MetroHub.Core.Services;

/// <summary>
/// Comprehensive diagnostic logger that writes to d:\MetroHub\hidden_diagnostics.log.
/// Tracks every aspect of memory: Managed Heap, Generations (0/1/2/LOH/POH), Fragmentation,
/// Private Committed Bytes, Working Set, Virtual Memory, GDI handles, USER handles, and Kernel handles.
/// </summary>
public static class HiddenDiagnosticsLogger
{
    public static bool IsEnabled { get; set; } = true;

    private static string _logFilePath = @"d:\MetroHub\hidden_diagnostics.log";
    private static readonly object _lock = new();
    private static volatile bool _isHubHidden = false;

    public static bool IsHubHidden => _isHubHidden;

    static HiddenDiagnosticsLogger()
    {
        try
        {
            string dir = Path.GetDirectoryName(_logFilePath) ?? string.Empty;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hidden_diagnostics.log");
            }
        }
        catch
        {
            _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hidden_diagnostics.log");
        }
    }

    public static void LogTransition(bool isVisible)
    {
        _isHubHidden = !isVisible;
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
            WriteEntry($"[TRANSITION] MetroHub is now {state} | WS: {wsMb:0.0} MB | Private: {privMb:0.0} MB | Managed: {heapMb:0.0} MB | GDI/USER: {gdi}/{user} | Handles: {handles}");
        }
        catch
        {
            // Diagnostics must never fail
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
        if (!_isHubHidden) return;

        double wsMb = GetWorkingSetMb();
        string detailStr = string.IsNullOrWhiteSpace(details) ? string.Empty : $" | Details: {details}";
        WriteEntry($"[HIDDEN ACTIVITY DETECTED] [{source}] {eventName} | Working Set: {wsMb:0.0} MB{detailStr}");
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
            sb.AppendLine($"==================== [MEMORY DIAGNOSTIC SNAPSHOT: {trigger}] ====================");
            sb.AppendLine($"  [OS & Process Memory]");
            sb.AppendLine($"    Working Set (Physical RAM):  {wsMb:0.0} MB  (Peak: {peakWsMb:0.0} MB)");
            sb.AppendLine($"    Private Bytes (True Commit): {privMb:0.0} MB  <-- [Crucial: tracks real unmanaged leaks]");
            sb.AppendLine($"    Paged Memory (Commit Size):  {pagedMb:0.0} MB  (Peak: {peakPagedMb:0.0} MB)");
            sb.AppendLine($"    Virtual Address Space:       {virtMb:0.0} MB");
            sb.AppendLine($"  [Handles & Win32 Resources]");
            sb.AppendLine($"    GDI Objects:                 {gdiCount} / 10,000  (Bitmaps, Pens, Brushes, DCs)");
            sb.AppendLine($"    USER Objects:                {userCount} / 10,000  (Windows, Menus, Hooks, Timers)");
            sb.AppendLine($"    Kernel Handles:              {handles}");
            sb.AppendLine($"    Active Threads:              {threads}");
            sb.AppendLine($"  [.NET Managed Heap (CLR GC)]");
            if (beforeManagedMb.HasValue)
            {
                double freed = beforeManagedMb.Value - currentManagedMb;
                sb.AppendLine($"    Managed Heap:                {beforeManagedMb.Value:0.0} MB → {currentManagedMb:0.0} MB (Freed: {freed:0.0} MB)");
            }
            else
            {
                sb.AppendLine($"    Live Allocated Objects:      {currentManagedMb:0.0} MB");
            }
            sb.AppendLine($"    Total Heap Size:             {heapSizeMb:0.0} MB");
            sb.AppendLine($"    Heap Fragmentation:          {fragmentedMb:0.0} MB ({fragPercent:0.0}%)");
            sb.AppendLine($"    Gen 0: {gen0Mb:0.0} MB  (Collections: {gen0Collections})");
            sb.AppendLine($"    Gen 1: {gen1Mb:0.0} MB  (Collections: {gen1Collections})");
            sb.AppendLine($"    Gen 2: {gen2Mb:0.0} MB  (Collections: {gen2Collections})");
            sb.AppendLine($"    LOH (Large Object Heap):     {lohMb:0.0} MB");
            sb.AppendLine($"    POH (Pinned Object Heap):    {pohMb:0.0} MB");
            sb.AppendLine($"  [WPF Runtime]");
            sb.AppendLine($"    Loaded Windows Count:        {windowCount}");
            sb.AppendLine($"==================================================================================");

            WriteEntry(sb.ToString().TrimEnd());
        }
        catch
        {
            // Diagnostics must never fail or throw
        }
    }

    private static double GetWorkingSetMb()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            return proc.WorkingSet64 / (1024.0 * 1024.0);
        }
        catch
        {
            return -1;
        }
    }

    private static void WriteEntry(string line)
    {
        try
        {
            string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(_logFilePath, entry);
            }
        }
        catch
        {
            // Diagnostics must never throw
        }
    }
}
