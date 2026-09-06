using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MetroHub.Core.Services;

/// <summary>
/// Industry-standard multi-tiered icon extraction engine.
/// Extracts crisp 256x256 / high-DPI vector-grade icons directly from
/// PE resource directories, Windows Shell Image Factory, and shortcuts,
/// eliminating the legacy 32x32 pixelated blur.
/// </summary>
public static class IconExtractorService
{
    private static readonly string IconCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MetroHub", "icons");

    private static readonly Guid IID_IShellItemImageFactory = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [Flags]
    public enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04,
        SIIGBF_THUMBNAILONLY = 0x08,
        SIIGBF_INCACHEONLY = 0x10,
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            [In] SIZE size,
            [In] SIIGBF flags,
            [Out] out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern uint PrivateExtractIcons(
        string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, IntPtr[] piconid, uint nIcons, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    static IconExtractorService()
    {
        try
        {
            if (!Directory.Exists(IconCacheDir))
            {
                Directory.CreateDirectory(IconCacheDir);
            }
        }
        catch { }
    }

    public static string? ExtractAndCacheIcon(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        try
        {
            string resolvedPath = ResolveShortcutTarget(filePath);
            string modernPath = ResolveModernAppRedirect(resolvedPath);
            string keyPath = !string.IsNullOrWhiteSpace(modernPath) ? modernPath : (!string.IsNullOrWhiteSpace(resolvedPath) ? resolvedPath : filePath);
            string hashName = $"{Math.Abs(keyPath.ToLowerInvariant().GetHashCode())}.png";
            string cachedFilePath = Path.Combine(IconCacheDir, hashName);

            // Fast header check: if already cached and high-res (>= 48px), return existing
            if (File.Exists(cachedFilePath))
            {
                try
                {
                    using var stream = new FileStream(cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth >= 48)
                    {
                        return cachedFilePath;
                    }
                }
                catch { }
            }

            // Extract best quality icon via multi-tier strategy
            BitmapSource? bs = ExtractBestQualityIcon(filePath, resolvedPath, modernPath);
            if (bs != null)
            {
                using (var fs = new FileStream(cachedFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bs));
                    encoder.Save(fs);
                }
                return cachedFilePath;
            }
        }
        catch { }

        return null;
    }

    private static BitmapSource? ExtractBestQualityIcon(string originalPath, string resolvedPath, string modernPath)
    {
        // Priority 0: If target resolves to a Modern Windows Store / Fluent App (e.g. Modern Notepad, Calculator, Settings)
        if (!string.Equals(modernPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            var modernIcon = ExtractViaShellItemImageFactory(modernPath, 256);
            if (modernIcon != null && modernIcon.PixelWidth >= 48) return modernIcon;
        }

        // Tier 1: PrivateExtractIcons (Direct PE 256x256 icon from executable or DLL)
        if (File.Exists(resolvedPath))
        {
            string ext = Path.GetExtension(resolvedPath).ToLowerInvariant();
            if (ext == ".exe" || ext == ".dll" || ext == ".ico")
            {
                var peIcon = ExtractViaPrivateExtractIcons(resolvedPath, 256);
                if (peIcon != null && peIcon.PixelWidth >= 48) return peIcon;
            }
        }

        // Tier 2: IShellItemImageFactory (Shell high-resolution icon/thumbnail engine)
        // Handles .lnk shortcuts, UWP shell:AppsFolder, document types, folders
        var shellItemIcon = ExtractViaShellItemImageFactory(originalPath, 256);
        if (shellItemIcon != null && shellItemIcon.PixelWidth >= 48) return shellItemIcon;

        if (!string.Equals(originalPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            shellItemIcon = ExtractViaShellItemImageFactory(resolvedPath, 256);
            if (shellItemIcon != null && shellItemIcon.PixelWidth >= 48) return shellItemIcon;
        }

        // Tier 3: PrivateExtractIcons with 48x48 fallback
        if (File.Exists(resolvedPath))
        {
            var pe48 = ExtractViaPrivateExtractIcons(resolvedPath, 48);
            if (pe48 != null) return pe48;
        }

        // Tier 4: Legacy Shell fallback (32x32)
        return ExtractViaLegacyShell(resolvedPath);
    }

    public static string ResolveModernAppRedirect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        string filename = Path.GetFileName(path).ToLowerInvariant();

        if (filename == "notepad.exe")
        {
            string modern = @"shell:AppsFolder\Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";
            if (ShellItemExists(modern)) return modern;
        }
        else if (filename == "calc.exe")
        {
            string modern = @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
            if (ShellItemExists(modern)) return modern;
        }
        else if (filename == "mspaint.exe")
        {
            string modern = @"shell:AppsFolder\Microsoft.Paint_8wekyb3d8bbwe!App";
            if (ShellItemExists(modern)) return modern;
        }
        else if (path.Equals("ms-settings:", StringComparison.OrdinalIgnoreCase) || filename == "settings")
        {
            return @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
        }

        return path;
    }

    private static bool ShellItemExists(string path)
    {
        try
        {
            Guid guid = IID_IShellItemImageFactory;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref guid, out var factory);
            return hr == 0 && factory != null;
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource? ExtractViaPrivateExtractIcons(string path, int size)
    {
        try
        {
            IntPtr[] phicon = new IntPtr[1];
            IntPtr[] piconid = new IntPtr[1];
            uint num = PrivateExtractIcons(path, 0, size, size, phicon, piconid, 1, 0);
            if (num > 0 && phicon[0] != IntPtr.Zero)
            {
                try
                {
                    var bs = Imaging.CreateBitmapSourceFromHIcon(
                        phicon[0],
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    return bs;
                }
                finally
                {
                    DestroyIcon(phicon[0]);
                }
            }
        }
        catch { }
        return null;
    }

    private static BitmapSource? ExtractViaShellItemImageFactory(string path, int size)
    {
        try
        {
            Guid guid = IID_IShellItemImageFactory;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref guid, out IShellItemImageFactory factory);
            if (hr == 0 && factory != null)
            {
                int hrImg = factory.GetImage(new SIZE(size, size), SIIGBF.SIIGBF_ICONONLY | SIIGBF.SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);
                if (hrImg == 0 && hBitmap != IntPtr.Zero)
                {
                    try
                    {
                        var bs = Imaging.CreateBitmapSourceFromHBitmap(
                            hBitmap,
                            IntPtr.Zero,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bs.Freeze();
                        return bs;
                    }
                    finally
                    {
                        DeleteObject(hBitmap);
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static BitmapSource? ExtractViaLegacyShell(string path)
    {
        try
        {
            SHFILEINFO shinfo = new SHFILEINFO();
            IntPtr hImg = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);
            if (shinfo.hIcon != IntPtr.Zero)
            {
                try
                {
                    var bs = Imaging.CreateBitmapSourceFromHIcon(
                        shinfo.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    return bs;
                }
                finally
                {
                    DestroyIcon(shinfo.hIcon);
                }
            }
        }
        catch { }
        return null;
    }

    public static string ResolveShortcutTarget(string shortcutPath)
    {
        if (!shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return shortcutPath;
        }

        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell != null)
                {
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    string target = shortcut.TargetPath;
                    Marshal.FinalReleaseComObject(shortcut);
                    Marshal.FinalReleaseComObject(shell);

                    if (!string.IsNullOrWhiteSpace(target) && (File.Exists(target) || Directory.Exists(target)))
                    {
                        return target;
                    }
                }
            }
        }
        catch { }

        return shortcutPath;
    }
}
