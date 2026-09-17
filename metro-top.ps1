<#
.SYNOPSIS
    MetroHub Live TUI Dashboard - A sleek, btop/htop-style command-line monitor.
#>

[CmdletBinding()]
param(
    [int]$IntervalMs = 1000,
    [int]$Count = 0 # 0 = infinite loop
)

# Output encoding
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$esc = [char]27

# Unicode box & block characters defined via char codes
$bTL   = [char]0x250C  # ┌
$bTR   = [char]0x2510  # ┐
$bBL   = [char]0x2514  # └
$bBR   = [char]0x2518  # ┘
$bH    = [char]0x2500  # ─
$bV    = [char]0x2502  # │
$bML   = [char]0x251C  # ├
$bMR   = [char]0x2524  # ┤
$bFull = [char]0x2588  # █
$bDim  = [char]0x2591  # ░
$bDot  = [char]0x25CF  # ●

# Sparkline levels
$sparkChars = @(
    [char]0x0020, # space
    [char]0x2581, #  
    [char]0x2582, # ▂
    [char]0x2583, # ▃
    [char]0x2584, # ▄
    [char]0x2585, # ▅
    [char]0x2586, # ▆
    [char]0x2587, # ▇
    [char]0x2588  # █
)

# Color Palette (ANSI 24-bit TrueColor)
$cReset       = "$esc[0m"
$cBold        = "$esc[1m"
$cDim         = "$esc[2m"
$cCyan        = "$esc[38;2;0;220;255m"
$cNeonGreen   = "$esc[38;2;0;255;150m"
$cGreen       = "$esc[38;2;75;215;100m"
$cYellow      = "$esc[38;2;255;205;60m"
$cOrange      = "$esc[38;2;255;135;40m"
$cRed         = "$esc[38;2;255;70;85m"
$cPurple      = "$esc[38;2;180;100;255m"
$cGray        = "$esc[38;2;110;120;135m"
$cDarkGray    = "$esc[38;2;60;68;80m"
$cWhite       = "$esc[38;2;240;245;250m"

# Hide Console Cursor via ANSI escape
[Console]::Write("$esc[?25l")

# Helper: Colorize based on percentage
function Get-PercentColor([double]$val, [double]$warn = 3.0, [double]$crit = 8.0) {
    if ($val -lt $warn) { return $cNeonGreen }
    if ($val -lt $crit) { return $cYellow }
    return $cRed
}

# Helper: Horizontal Progress Bar
function Get-ProgressBar([double]$val, [double]$maxVal = 10.0, [int]$width = 24) {
    $ratio = [Math]::Clamp(($val / $maxVal), 0.0, 1.0)
    $filled = [int][Math]::Round($ratio * $width)
    $unfilled = $width - $filled
    $barCol = Get-PercentColor -val $val
    $fStr = ("" + $bFull) * $filled
    $uStr = ("" + $bDim) * $unfilled
    return "$barCol$fStr$cDarkGray$uStr$cReset"
}

# Helper: Sparkline renderer from history
function Get-Sparkline([double[]]$history, [double]$maxScale = 8.0) {
    if (-not $history -or $history.Length -eq 0) { return "" }
    $sb = [System.Text.StringBuilder]::new()
    foreach ($val in $history) {
        $ratio = [Math]::Clamp(($val / $maxScale), 0.0, 1.0)
        $idx = [int][Math]::Floor($ratio * ($sparkChars.Length - 1))
        $col = Get-PercentColor -val $val
        [void]$sb.Append("$col$($sparkChars[$idx])$cReset")
    }
    return $sb.ToString()
}

# Metric History State
$historyLimit = 36
$cpuHistory = [System.Collections.Generic.List[double]]::new()
$peakCpu = 0.0
$totalCpuSum = 0.0
$sampleCount = 0
$cores = [Environment]::ProcessorCount

# Precompute border lines
$barTop = "$cCyan$bTL" + ("" + $bH) * 72 + "$bTR$cReset"
$barMid = "$cCyan$bML" + ("" + $bH) * 72 + "$bMR$cReset"
$barBot = "$cCyan$bBL" + ("" + $bH) * 72 + "$bBR$cReset"

# Clear screen once at startup
[Console]::Write("$esc[2J$esc[H")

$iter = 0

try {
    while ($true) {
        if ($Count -gt 0 -and $iter -ge $Count) { break }
        $iter++

        # Check for keyboard inputs safely
        try {
            if ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true).Key
                if ($key -eq [ConsoleKey]::Q -or $key -eq [ConsoleKey]::Escape) {
                    break
                }
                if ($key -eq [ConsoleKey]::R) {
                    $cpuHistory.Clear()
                    $peakCpu = 0.0
                    $totalCpuSum = 0.0
                    $sampleCount = 0
                }
                if ($key -eq [ConsoleKey]::T) {
                    $IntervalMs = if ($IntervalMs -eq 1000) { 500 } else { 1000 }
                }
            }
        } catch { }

        # Locate MetroHub process
        $proc = Get-Process -Name MetroHub -ErrorAction SilentlyContinue | Select-Object -First 1

        if (-not $proc) {
            $msg = @(
                $barTop,
                "$cCyan$bV$cBold$cWhite   ⚡  METROHUB TUI MONITOR                                         $cCyan$bV$cReset",
                $barMid,
                "$cCyan$bV$cReset  $cYellow⏳ Waiting for MetroHub.exe to start...                               $cCyan$bV$cReset",
                "$cCyan$bV$cReset  $cGray(Launch MetroHub or press Q to exit)                                 $cCyan$bV$cReset",
                $barBot
            ) -join "`n"

            [Console]::Write("$esc[H$msg`n")
            Start-Sleep -Milliseconds 1000
            continue
        }

        # Measure CPU delta over interval
        $prevCpu = $proc.CPU
        $prevTime = [DateTime]::UtcNow

        Start-Sleep -Milliseconds $IntervalMs

        $proc.Refresh()
        if ($proc.HasExited) { continue }

        $currCpu = $proc.CPU
        $currTime = [DateTime]::UtcNow

        $elapsedSec = ($currTime - $prevTime).TotalSeconds
        $cpuDeltaSec = [Math]::Max(0.0, $currCpu - $prevCpu)
        $cpuPercent = if ($elapsedSec -gt 0) { ($cpuDeltaSec / ($elapsedSec * $cores)) * 100.0 } else { 0.0 }
        
        $cpuPercent = [Math]::Round($cpuPercent, 2)

        # Update History & Stats
        $cpuHistory.Add($cpuPercent)
        if ($cpuHistory.Count -gt $historyLimit) {
            $cpuHistory.RemoveAt(0)
        }
        if ($cpuPercent -gt $peakCpu) { $peakCpu = $cpuPercent }
        $totalCpuSum += $cpuPercent
        $sampleCount++
        $avgCpu = [Math]::Round(($totalCpuSum / $sampleCount), 2)

        # Process Metrics
        $ramMb = [Math]::Round($proc.WorkingSet64 / 1MB, 1)
        $peakRamMb = [Math]::Round($proc.PeakWorkingSet64 / 1MB, 1)
        $privateMb = [Math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
        $threads = $proc.Threads.Count
        $handles = $proc.Handles
        $uptime = [DateTime]::Now - $proc.StartTime
        $uptimeStr = "{0:D2}h {1:D2}m {2:D2}s" -f [int]$uptime.TotalHours, $uptime.Minutes, $uptime.Seconds

        $statusStr = if ($proc.Responding) { "$cNeonGreen$bDot RUNNING$cReset" } else { "$cRed$bDot HUNG$cReset" }
        $cpuCol = Get-PercentColor -val $cpuPercent
        $cpuBar = Get-ProgressBar -val $cpuPercent -maxVal 8.0 -width 24
        $ramBar = Get-ProgressBar -val $ramMb -maxVal 100.0 -width 24
        $spark = Get-Sparkline -history ($cpuHistory.ToArray()) -maxScale 8.0
        
        $missingSpark = $historyLimit - $cpuHistory.Count
        if ($missingSpark -gt 0) {
            $spark = ("$cDarkGray.$cReset" * $missingSpark) + $spark
        }

        # Format rows cleanly
        $r1 = "$cCyan$bV$cBold$cWhite  ⚡ METROHUB PERFORMANCE MONITOR                 $cReset$cGray[Tick: $($IntervalMs)ms]$cCyan      $bV$cReset"
        $r2 = "$cCyan$bV$cReset  PID: $cWhite$($proc.Id.ToString().PadRight(7))$cReset Status: $statusStr$cReset  Uptime: $cWhite$uptimeStr$cReset   Cores: $cWhite$cores$cCyan  $bV$cReset"
        $r3 = "$cCyan$bV$cBold$cWhite  PROCESSOR (CPU)$cReset                                                    $cCyan$bV$cReset"
        $r4 = "$cCyan$bV$cReset  Usage:  $cpuCol$($cpuPercent.ToString("0.00").PadLeft(6))%$cReset  [$cpuBar$cReset]  $cGray(Max: 8%)$cCyan        $bV$cReset"
        $r5 = "$cCyan$bV$cReset  Stats:  Avg: $cWhite$($avgCpu.ToString("0.00").PadLeft(5))%$cReset  Peak: $cWhite$($peakCpu.ToString("0.00").PadLeft(5))%$cReset  Samples: $cWhite$($sampleCount.ToString().PadRight(6))$cReset$cCyan$bV$cReset"
        $r6 = "$cCyan$bV$cReset  Chart:  [$spark$cReset] $cGray(Last 36s)$cCyan      $bV$cReset"
        $r7 = "$cCyan$bV$cBold$cWhite  MEMORY & SYSTEM$cReset                                                    $cCyan$bV$cReset"
        $r8 = "$cCyan$bV$cReset  RAM:    $cNeonGreen$($ramMb.ToString("0.0").PadLeft(6)) MB$cReset [$ramBar$cReset]  $cGray(Goal: <100MB)$cCyan   $bV$cReset"
        $r9 = "$cCyan$bV$cReset  Peak:   $cWhite$($peakRamMb.ToString("0.0").PadLeft(6)) MB$cReset  Private: $cWhite$($privateMb.ToString("0.0").PadLeft(6)) MB$cReset  Threads: $cWhite$($threads.ToString().PadLeft(3))$cReset  Handles: $cWhite$($handles.ToString().PadLeft(5))$cReset$cCyan$bV$cReset"
        $help = "$cDim  [Q / Esc] Quit    [R] Reset Stats    [T] Toggle 500ms/1000ms$cReset"

        $output = @(
            $barTop,
            $r1,
            $barMid,
            $r2,
            $barMid,
            $r3,
            $r4,
            $r5,
            $r6,
            $barMid,
            $r7,
            $r8,
            $r9,
            $barBot,
            $help
        ) -join "`n"

        # ANSI home position + atomic write for 100% flicker-free rendering
        [Console]::Write("$esc[H$output`n")
    }
}
finally {
    # Restore Console Cursor
    [Console]::Write("$esc[?25h")
    Write-Host "$cGray[Monitor stopped]$cReset"
}
