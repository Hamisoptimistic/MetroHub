using System;
using System.Globalization;

namespace MetroHub.Core.Common;

/// <summary>
/// High-performance, centralized formatting utilities for bytes, transfer rates, and durations.
/// Eliminates duplicate string formatting math across widgets and core services.
/// </summary>
public static class FormatUtils
{
    private const ulong OneKb = 1024UL;
    private const ulong OneMb = OneKb * 1024UL;
    private const ulong OneGb = OneMb * 1024UL;
    private const ulong OneTb = OneGb * 1024UL;

    public static string FormatBytes(ulong bytes)
    {
        return bytes switch
        {
            >= OneTb => string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / OneTb:0.00} TB"),
            >= OneGb => string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / OneGb:0.00} GB"),
            >= OneMb => string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / OneMb:0.0} MB"),
            >= OneKb => string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / OneKb:0} KB"),
            _ => $"{bytes} B"
        };
    }

    public static string FormatBytes(long bytes) => FormatBytes(bytes < 0 ? 0UL : (ulong)bytes);

    public static string FormatBytesSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 0) bytesPerSec = 0;

        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;

        return bytesPerSec switch
        {
            >= gb => string.Create(CultureInfo.InvariantCulture, $"{bytesPerSec / gb:0.00} GB/s"),
            >= mb => string.Create(CultureInfo.InvariantCulture, $"{bytesPerSec / mb:0.0} MB/s"),
            >= kb => string.Create(CultureInfo.InvariantCulture, $"{bytesPerSec / kb:0} KB/s"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytesPerSec:0} B/s")
        };
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "00:00:00";
        if (duration.TotalDays >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalDays}d {duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}");
    }
}
