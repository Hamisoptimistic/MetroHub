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

# VT / ANSI Sequences
$smcup       = "$esc[?1049h" # Switch to alternate screen buffer
$rmcup       = "$esc[?1049l" # Restore primary screen buffer
$hideCursor  = "$esc[?25l"
$showCursor  = "$esc[?25h"
$disableWrap = "$esc[?7l"    # Disable auto-wrap (prevents row displacement)
$enableWrap  = "$esc[?7h"
$clearScreen = "$esc[2J"
$homeCursor  = "$esc[H"
$clearEol    = "$esc[K"     # Erase to end of line
$clearEos    = "$esc[J"     # Erase to end of screen

# Unicode box & block characters
$bTL   = [char]0x250C  # ┌
$bTR   = [char]0x2510  # ┐
$bBL   = [char]0x2514  # └
$bBR   = [char]0x2518  # ┘
$bH    = "" + [char]0x2500  # ─
$bV    = "" + [char]0x2502  # │
$bML   = [char]0x251C  # ├
$bMR   = [char]0x2524  # ┤
$bFull = "" + [char]0x2588  # █
$bDim  = "" + [char]0x2591  # ░
$bDot  = "" + [char]0x25CF  # ●

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

# Layout Constants
$InnerWidth   = 72
$minReqWidth  = $InnerWidth + 2  # 74 columns total width with borders

# Helper: Safely get terminal dimensions without crashing in redirected/headless hosts
function Get-ConsoleDimensions {
    $w = 80
    $h = 25
    try {
        $w = [Console]::WindowWidth
        $h = [Console]::WindowHeight
    } catch { }
    return @{ Width = $w; Height = $h }
}

# Helper: Format Row with guaranteed border alignment, left margin, and right EOL cleanup
function Format-TuiRow([string]$text, [string]$leftPad = "") {
    $clean = $text -replace '\x1B\[[0-9;]*[a-zA-Z]', ''
    $padLen = [Math]::Max(0, $InnerWidth - $clean.Length)
    $pad = " " * $padLen
    return "$leftPad$cCyan$bV$cReset$text$pad$cCyan$bV$cReset$clearEol"
}

# Helper: Colorize based on percentage
function Get-PercentColor([double]$val, [double]$warn = 3.0, [double]$crit = 8.0) {
    if ($val -lt $warn) { return $cNeonGreen }
    if ($val -lt $crit) { return $cYellow }
    return $cRed
}

# Helper: Horizontal Progress Bar
function Get-ProgressBar([double]$val, [double]$maxVal = 10.0, [int]$width = 24) {
    $ratio = [Math]::Min(1.0, [Math]::Max(0.0, ($val / $maxVal)))
    $filled = [int][Math]::Round($ratio * $width)
    $unfilled = $width - $filled
    $barCol = Get-PercentColor -val $val
    $fStr = $bFull * $filled
    $uStr = $bDim * $unfilled
    return "$barCol$fStr$cDarkGray$uStr$cReset"
}

# Helper: Sparkline renderer from history
function Get-Sparkline([double[]]$history, [double]$maxScale = 8.0) {
    if (-not $history -or $history.Length -eq 0) { return "" }
    $sb = [System.Text.StringBuilder]::new()
    foreach ($val in $history) {
        $ratio = [Math]::Min(1.0, [Math]::Max(0.0, ($val / $maxScale)))
        $idx = [int][Math]::Floor($ratio * ($sparkChars.Length - 1))
        $col = Get-PercentColor -val $val
        [void]$sb.Append("$col$($sparkChars[$idx])$cReset")
    }
    return $sb.ToString()
}

# Metric History State
$historyLimit   = 36
$cpuHistory     = [System.Collections.Generic.List[double]]::new()
$gpuHistory     = [System.Collections.Generic.List[double]]::new()
$peakCpu        = 0.0
$peakGpu        = 0.0
$totalCpuSum    = 0.0
$totalGpuSum    = 0.0
$sampleCount    = 0
$gpuSampleCount = 0
$cores          = [Environment]::ProcessorCount

# Process & Counter State
$iter           = 0
$privWsCounter  = $null
$gpuCounters    = [System.Collections.Generic.List[System.Diagnostics.PerformanceCounter]]::new()
$lastPid        = 0

# Track previous terminal dimensions for dynamic resize handling
$lastW = 0
$lastH = 0

function Clear-GpuCounters {
    foreach ($c in $gpuCounters) {
        try { $c.Dispose() } catch { }
    }
    $gpuCounters.Clear()
}

# Enter alternate screen buffer + hide cursor + disable auto-wrap
[Console]::Write("$smcup$disableWrap$hideCursor$clearScreen$homeCursor")

try {
    while ($true) {
        if ($Count -gt 0 -and $iter -ge $Count) { break }
        $iter++

        # Terminal Window Dimensions & Dynamic Resize Detection
        $dims = Get-ConsoleDimensions
        $curW = $dims.Width
        $curH = $dims.Height

        if ($curW -ne $lastW -or $curH -ne $lastH) {
            $lastW = $curW
            $lastH = $curH
            # Complete screen wipe when terminal size changes to avoid artifact ghosts
            [Console]::Write("$clearScreen$homeCursor")
        }

        # Check for keyboard inputs safely
        try {
            if ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true).Key
                if ($key -eq [ConsoleKey]::Q -or $key -eq [ConsoleKey]::Escape) {
                    break
                }
                if ($key -eq [ConsoleKey]::R) {
                    $cpuHistory.Clear()
                    $gpuHistory.Clear()
                    $peakCpu = 0.0
                    $peakGpu = 0.0
                    $totalCpuSum = 0.0
                    $totalGpuSum = 0.0
                    $sampleCount = 0
                    $gpuSampleCount = 0
                }
                if ($key -eq [ConsoleKey]::T) {
                    $IntervalMs = if ($IntervalMs -eq 1000) { 500 } else { 1000 }
                }
            }
        } catch { }

        # Centering Margin (Keeps TUI centered regardless of terminal width)
        $leftMargin = [Math]::Max(0, [int](($curW - $minReqWidth) / 2))
        $leftPad = " " * $leftMargin

        $barTop = "$leftPad$cCyan$bTL" + ($bH * $InnerWidth) + "$bTR$cReset$clearEol"
        $barMid = "$leftPad$cCyan$bML" + ($bH * $InnerWidth) + "$bMR$cReset$clearEol"
        $barBot = "$leftPad$cCyan$bBL" + ($bH * $InnerWidth) + "$bBR$cReset$clearEol"

        # Locate MetroHub process
        $proc = Get-Process -Name MetroHub -ErrorAction SilentlyContinue | Select-Object -First 1

        if (-not $proc) {
            if ($privWsCounter) {
                try { $privWsCounter.Dispose() } catch { }
                $privWsCounter = $null
                $lastPid = 0
            }
            Clear-GpuCounters

            $msg = @(
                $barTop,
                (Format-TuiRow "  $cBold$cWhite METROHUB TUI MONITOR$cReset" $leftPad),
                $barMid,
                (Format-TuiRow "  $cYellow Waiting for MetroHub.exe to start...$cReset" $leftPad),
                (Format-TuiRow "  $cGray(Launch MetroHub or press Q to exit)$cReset" $leftPad),
                $barBot
            ) -join "`n"

            [Console]::Write("$homeCursor$msg$clearEos")
            Start-Sleep -Milliseconds 1000
            continue
        }

        # Initialize or recreate Private Working Set counter on process PID change
        if (-not $privWsCounter -or $lastPid -ne $proc.Id) {
            if ($privWsCounter) {
                try { $privWsCounter.Dispose() } catch { }
                $privWsCounter = $null
            }
            Clear-GpuCounters
            try {
                $privWsCounter = New-Object System.Diagnostics.PerformanceCounter('Process', 'Working Set - Private', 'MetroHub', $true)
                [void]$privWsCounter.NextValue()
                $lastPid = $proc.Id
            } catch {
                $privWsCounter = $null
            }
        }

        # Initialize GPU 3D engine counters for target process if not yet connected
        if ($gpuCounters.Count -eq 0) {
            try {
                $gpuCat = [System.Diagnostics.PerformanceCounterCategory]::new('GPU Engine')
                $gpuInstances = $gpuCat.GetInstanceNames() | Where-Object { $_ -like "*pid_$($proc.Id)*engtype_3D*" }
                foreach ($inst in $gpuInstances) {
                    $c = [System.Diagnostics.PerformanceCounter]::new('GPU Engine', 'Utilization Percentage', $inst)
                    [void]$c.NextValue() # Prime the counter
                    $gpuCounters.Add($c)
                }
            } catch { }
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

        # Update CPU History & Stats
        $cpuHistory.Add($cpuPercent)
        if ($cpuHistory.Count -gt $historyLimit) {
            $cpuHistory.RemoveAt(0)
        }
        if ($cpuPercent -gt $peakCpu) { $peakCpu = $cpuPercent }
        $totalCpuSum += $cpuPercent
        $sampleCount++
        $avgCpu = [Math]::Round(($totalCpuSum / $sampleCount), 2)

        # Sample GPU 3D engine utilization
        $gpuPercent = 0.0
        if ($gpuCounters.Count -gt 0) {
            try {
                foreach ($c in $gpuCounters) {
                    $gpuPercent += $c.NextValue()
                }
            } catch {
                Clear-GpuCounters
            }
        }
        $gpuPercent = [Math]::Round($gpuPercent, 2)

        # Update GPU History & Stats
        $gpuHistory.Add($gpuPercent)
        if ($gpuHistory.Count -gt $historyLimit) {
            $gpuHistory.RemoveAt(0)
        }
        if ($gpuPercent -gt $peakGpu) { $peakGpu = $gpuPercent }
        $totalGpuSum += $gpuPercent
        $gpuSampleCount++
        $avgGpu = if ($gpuSampleCount -gt 0) { [Math]::Round(($totalGpuSum / $gpuSampleCount), 2) } else { 0.0 }

        # Process Memory Metrics
        $privateWsMb = if ($privWsCounter) {
            try { [Math]::Round($privWsCounter.NextValue() / 1MB, 1) } catch { 0.0 }
        } else { 0.0 }

        $ramMb = [Math]::Round($proc.WorkingSet64 / 1MB, 1)
        if ($privateWsMb -le 0.0) { $privateWsMb = $ramMb }
        $sharedMb = [Math]::Max(0.0, [Math]::Round($ramMb - $privateWsMb, 1))
        $peakRamMb = [Math]::Round($proc.PeakWorkingSet64 / 1MB, 1)
        $privateMb = [Math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
        $threads = $proc.Threads.Count
        $handles = $proc.Handles
        $uptime = [DateTime]::Now - $proc.StartTime
        $uptimeStr = "{0:D2}h {1:D2}m {2:D2}s" -f [int]$uptime.TotalHours, $uptime.Minutes, $uptime.Seconds

        $statusStr = if ($proc.Responding) { "$cNeonGreen$bDot RUNNING$cReset" } else { "$cRed$bDot HUNG$cReset" }

        # CPU Visualization
        $cpuCol = Get-PercentColor -val $cpuPercent
        $cpuBar = Get-ProgressBar -val $cpuPercent -maxVal 8.0 -width 24
        $cpuSpark = Get-Sparkline -history ($cpuHistory.ToArray()) -maxScale 8.0
        $missingCpuSpark = $historyLimit - $cpuHistory.Count
        if ($missingCpuSpark -gt 0) {
            $cpuSpark = ("$cDarkGray.$cReset" * $missingCpuSpark) + $cpuSpark
        }

        # GPU Visualization
        $gpuCol = Get-PercentColor -val $gpuPercent -warn 10.0 -crit 25.0
        $gpuBar = Get-ProgressBar -val $gpuPercent -maxVal 20.0 -width 24
        $gpuSpark = Get-Sparkline -history ($gpuHistory.ToArray()) -maxScale 20.0
        $missingGpuSpark = $historyLimit - $gpuHistory.Count
        if ($missingGpuSpark -gt 0) {
            $gpuSpark = ("$cDarkGray.$cReset" * $missingGpuSpark) + $gpuSpark
        }

        # Memory Visualization
        $ramBar = Get-ProgressBar -val $privateWsMb -maxVal 100.0 -width 24

        # Format rows
        $r1  = Format-TuiRow "  $cBold$cWhite METROHUB PERFORMANCE MONITOR$cReset$cGray                   [Tick: $($IntervalMs)ms]$cReset" $leftPad
        $r2  = Format-TuiRow "  PID: $cWhite$($proc.Id.ToString().PadRight(6))$cReset Status: $statusStr$cReset   Uptime: $cWhite$($uptimeStr)$cReset   Cores: $cWhite$($cores)$cReset" $leftPad

        # CPU Card
        $r3  = Format-TuiRow "  $cBold$cWhite PROCESSOR (CPU)$cReset" $leftPad
        $r4  = Format-TuiRow "  Usage:    $cpuCol$($cpuPercent.ToString('0.00').PadLeft(6))%$cReset  [$cpuBar]  $cGray(Target: <8%)$cReset" $leftPad
        $r5  = Format-TuiRow "  Stats:    Avg: $cWhite$($avgCpu.ToString('0.00').PadLeft(5))%$cReset  Peak: $cWhite$($peakCpu.ToString('0.00').PadLeft(5))%$cReset  Samples: $cWhite$($sampleCount.ToString().PadRight(6))$cReset" $leftPad
        $r6  = Format-TuiRow "  Chart:    [$cpuSpark] $cGray(Last 36s)$cReset" $leftPad

        # GPU Card
        $rg1 = Format-TuiRow "  $cBold$cWhite GRAPHICS (GPU - 3D ENGINE)$cReset" $leftPad
        $rg2 = Format-TuiRow "  Usage:    $gpuCol$($gpuPercent.ToString('0.00').PadLeft(6))%$cReset  [$gpuBar]  $cGray(Target: <15%)$cReset" $leftPad
        $rg3 = Format-TuiRow "  Stats:    Avg: $cWhite$($avgGpu.ToString('0.00').PadLeft(5))%$cReset  Peak: $cWhite$($peakGpu.ToString('0.00').PadLeft(5))%$cReset  Engines: $cWhite$($gpuCounters.Count.ToString().PadRight(6))$cReset" $leftPad
        $rg4 = Format-TuiRow "  Chart:    [$gpuSpark] $cGray(Last 36s)$cReset" $leftPad

        # Memory Card
        $r7  = Format-TuiRow "  $cBold$cWhite MEMORY & SYSTEM$cReset" $leftPad
        $r8  = Format-TuiRow "  RAM:      $cNeonGreen$($privateWsMb.ToString('0.0').PadLeft(6)) MB$cReset  [$ramBar]  $cGray(Task Mgr Private)$cReset" $leftPad
        $r9  = Format-TuiRow "  Total WS: $cWhite$($ramMb.ToString('0.0').PadLeft(6)) MB$cReset  Shared: $cWhite$($sharedMb.ToString('0.0').PadLeft(6)) MB$cReset  Commit: $cWhite$($privateMb.ToString('0.0').PadLeft(6)) MB$cReset" $leftPad
        $r10 = Format-TuiRow "  Peak WS:  $cWhite$($peakRamMb.ToString('0.0').PadLeft(6)) MB$cReset  Threads: $cWhite$($threads.ToString().PadLeft(3))$cReset       Handles: $cWhite$($handles.ToString().PadLeft(5))$cReset" $leftPad

        $help = "$leftPad$cDim  [Q / Esc] Quit    [R] Reset Stats    [T] Toggle 500ms/1000ms$cReset$clearEol"

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
            $rg1,
            $rg2,
            $rg3,
            $rg4,
            $barMid,
            $r7,
            $r8,
            $r9,
            $r10,
            $barBot,
            $help
        ) -join "`n"

        # Atomic write: home cursor, render frame, and wipe any trailing terminal content
        [Console]::Write("$homeCursor$output$clearEos")
    }
}
finally {
    if ($privWsCounter) {
        try { $privWsCounter.Dispose() } catch { }
        $privWsCounter = $null
    }
    Clear-GpuCounters

    # Restore terminal: enable wrap, show cursor, exit alternate screen buffer
    [Console]::Write("$enableWrap$showCursor$rmcup")
    Write-Host "$cGray[Monitor stopped]$cReset"
}
