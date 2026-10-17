using System;
using System.Runtime.InteropServices;

namespace MetroHub.Core.Search.Native;

/// <summary>
/// Native P/Invoke methods specialized for Universal Search operations.
/// Follows existing MetroHub interop patterns using standard DllImport.
/// </summary>
internal static class NativeMethodsSearch
{
    public const uint SHERB_NOCONFIRMATION = 0x00000001;
    public const uint SHERB_NOPROGRESSUI   = 0x00000002;
    public const uint SHERB_NOSOUND        = 0x00000004;

    /// <summary>
    /// Empties the Windows Recycle Bin on all drives or a specific root.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}

