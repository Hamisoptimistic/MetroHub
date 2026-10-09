using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace MetroHub.Core.Services;

/// <summary>
/// Provides resilient Windows clipboard access with exponential backoff retries,
/// COM exception handling (CLIPBRD_E_CANT_OPEN), sensitive data detection,
/// and cross-thread safe bitmap extraction.
/// </summary>
public static class SafeClipboard
{
    public const int DefaultMaxRetries = 5;
    public const int DefaultInitialDelayMs = 20;

    /// <summary>
    /// Standard Windows/password-manager clipboard format indicating data must not be recorded or monitored.
    /// </summary>
    public const string FormatExcludeFromMonitor = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>
    /// Windows 10+ Cloud/History clipboard format. When integer 0, history is prohibited.
    /// </summary>
    public const string FormatCanIncludeInHistory = "CanIncludeInClipboardHistory";

    /// <summary>
    /// Queries the Windows clipboard on the calling STA thread with exponential backoff.
    /// Returns null if access is denied or the clipboard remains locked by another process.
    /// </summary>
    public static IDataObject? GetDataObject(int maxRetries = DefaultMaxRetries, int initialDelayMs = DefaultInitialDelayMs)
    {
        int delayMs = initialDelayMs;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return Clipboard.GetDataObject();
            }
            catch (COMException ex) when ((uint)ex.ErrorCode == 0x800401D0) // CLIPBRD_E_CANT_OPEN
            {
                if (attempt == maxRetries - 1)
                {
                    Safe.Log(ex, "SafeClipboard: Clipboard locked by external process (CLIPBRD_E_CANT_OPEN). Retries exhausted.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries - 1)
                {
                    Safe.Log(ex, "SafeClipboard: Unexpected clipboard access failure. Retries exhausted.");
                    return null;
                }
            }

            Thread.Sleep(delayMs);
            delayMs *= 2;
        }

        return null;
    }

    /// <summary>
    /// Legacy clipboard format used by password managers to instruct tools not to record clipboard content.
    /// </summary>
    public const string FormatClipboardViewerIgnore = "Clipboard Viewer Ignore";

    /// <summary>
    /// Checks if the clipboard payload contains privacy or security flags set by password managers
    /// or confidential applications, prohibiting persistent storage.
    /// </summary>
    public static bool IsSensitiveData(IDataObject? dataObject)
    {
        if (dataObject == null) return false;

        try
        {
            // Format presence alone indicates confidential/sensitive content
            if (dataObject.GetDataPresent(FormatExcludeFromMonitor) ||
                dataObject.GetDataPresent(FormatClipboardViewerIgnore))
            {
                return true;
            }

            if (dataObject.GetDataPresent(FormatCanIncludeInHistory))
            {
                var val = dataObject.GetData(FormatCanIncludeInHistory);
                int dword = ParseDwordValue(val);
                // When 0, password managers or apps explicitly prohibit recording to history/disk
                if (dword == 0) return true;
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "SafeClipboard.IsSensitiveData format inspection failed");
        }

        return false;
    }

    private static int ParseDwordValue(object? val)
    {
        if (val is int intVal) return intVal;
        if (val is bool boolVal) return boolVal ? 1 : 0;
        if (val is byte byteVal) return byteVal;
        if (val is System.IO.MemoryStream ms)
        {
            byte[] bytes = ms.ToArray();
            if (bytes.Length >= 4) return BitConverter.ToInt32(bytes, 0);
            if (bytes.Length > 0) return bytes[0];
        }
        if (val is byte[] rawBytes)
        {
            if (rawBytes.Length >= 4) return BitConverter.ToInt32(rawBytes, 0);
            if (rawBytes.Length > 0) return rawBytes[0];
        }
        return -1;
    }

    /// <summary>
    /// Safely retrieves an image from the clipboard and immediately freezes it
    /// so it can be passed across background worker threads without Dispatcher violations.
    /// </summary>
    public static BitmapSource? TryGetFrozenImage(IDataObject? dataObject)
    {
        if (dataObject == null) return null;

        try
        {
            BitmapSource? source = null;

            if (dataObject.GetDataPresent(DataFormats.Bitmap))
            {
                source = dataObject.GetData(DataFormats.Bitmap) as BitmapSource;
            }

            if (source == null && Clipboard.ContainsImage())
            {
                source = Clipboard.GetImage();
            }

            if (source != null)
            {
                if (source.CanFreeze && !source.IsFrozen)
                {
                    source.Freeze();
                }
                return source;
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "SafeClipboard.TryGetFrozenImage extraction failed");
        }

        return null;
    }
}
