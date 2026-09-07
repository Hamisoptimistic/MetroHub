using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _iconPathCache = new(StringComparer.OrdinalIgnoreCase);

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    private static readonly Dictionary<string, string> KnownFolderMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) },
        { "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) },
        { "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.GetFolderPath(Environment.SpecialFolder.System) },
        { "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) },
        { "{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.GetFolderPath(Environment.SpecialFolder.Windows) },
        { "{905e63b6-c1bf-494e-b29c-65b732d3d21a}", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles) },
        { "{DE974928-267F-4E40-A6DA-8C323A0DEC07}", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86) },
        { "{5E6C858F-0E22-4760-9AFE-EA3317B67173}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) },
        { "{A52BBA46-E9E1-435F-B3D9-28DAA648C0F6}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) },
        { "{3EB685FD-984F-4E40-B0D2-E4531642D541}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) }
    };

    public static string ResolveKnownFolderGuid(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        string stripped = path;
        if (stripped.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
        {
            stripped = stripped.Substring("shell:AppsFolder\\".Length);
        }

        foreach (var kvp in KnownFolderMap)
        {
            if (stripped.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                string remainder = stripped.Substring(kvp.Key.Length).TrimStart('\\', '/');
                string resolved = Path.Combine(kvp.Value, remainder);
                if (File.Exists(resolved) || Directory.Exists(resolved))
                {
                    return resolved;
                }
            }
        }

        if (stripped.StartsWith("{"))
        {
            int endBrace = stripped.IndexOf('}');
            if (endBrace > 0)
            {
                string guidStr = stripped.Substring(1, endBrace - 1);
                if (Guid.TryParse(guidStr, out Guid rfid))
                {
                    int hr = SHGetKnownFolderPath(rfid, 0, IntPtr.Zero, out IntPtr ppszPath);
                    if (hr == 0 && ppszPath != IntPtr.Zero)
                    {
                        try
                        {
                            string? folderPath = Marshal.PtrToStringUni(ppszPath);
                            if (!string.IsNullOrWhiteSpace(folderPath))
                            {
                                string remainder = stripped.Substring(endBrace + 1).TrimStart('\\', '/');
                                string resolved = Path.Combine(folderPath, remainder);
                                if (File.Exists(resolved) || Directory.Exists(resolved))
                                {
                                    return resolved;
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(ppszPath);
                        }
                    }
                }
            }
        }

        return path;
    }

    public static BitmapSource? TrimTransparentPadding(BitmapSource? source, double paddingPercent = 0.05)
    {
        if (source == null) return null;

        try
        {
            int width = source.PixelWidth;
            int height = source.PixelHeight;
            if (width <= 16 || height <= 16) return source;

            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            formatted.CopyPixels(pixels, stride, 0);

            // Step 1: Find solid content bounds (A >= 100) to ignore faint shell backplates, tiles, and drop shadows
            int minX = width, minY = height, maxX = -1, maxY = -1;

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte alpha = pixels[rowOffset + (x * 4) + 3];
                    if (alpha >= 100)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            // Fallback: if no pixels with A >= 100 (e.g. translucent icon), check with A >= 30
            if (maxX < minX || maxY < minY)
            {
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        byte alpha = pixels[rowOffset + (x * 4) + 3];
                        if (alpha >= 30)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
            }

            // If completely empty, return original
            if (maxX < minX || maxY < minY) return source;

            // Expand slightly to preserve smooth anti-aliased edges around the solid core
            minX = Math.Max(0, minX - 2);
            minY = Math.Max(0, minY - 2);
            maxX = Math.Min(width - 1, maxX + 2);
            maxY = Math.Min(height - 1, maxY + 2);

            int contentW = maxX - minX + 1;
            int contentH = maxY - minY + 1;

            // If the icon content already fills >= 80% of canvas, no need to crop
            if (contentW >= width * 0.80 && contentH >= height * 0.80)
            {
                return source;
            }

            // Calculate centered square crop area with a subtle breathing padding
            int maxDim = Math.Max(contentW, contentH);
            int pad = Math.Max(1, (int)Math.Round(maxDim * paddingPercent));
            int targetSize = maxDim + (pad * 2);

            int centerX = minX + (contentW / 2);
            int centerY = minY + (contentH / 2);

            int cropX = Math.Max(0, centerX - (targetSize / 2));
            int cropY = Math.Max(0, centerY - (targetSize / 2));

            if (cropX + targetSize > width) cropX = Math.Max(0, width - targetSize);
            if (cropY + targetSize > height) cropY = Math.Max(0, height - targetSize);

            int finalW = Math.Min(targetSize, width - cropX);
            int finalH = Math.Min(targetSize, height - cropY);

            if (finalW <= 0 || finalH <= 0) return source;

            var cropped = new CroppedBitmap(formatted, new Int32Rect(cropX, cropY, finalW, finalH));
            cropped.Freeze();
            return cropped;
        }
        catch
        {
            return source;
        }
    }

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

        if (_iconPathCache.TryGetValue(filePath, out var memoized) && File.Exists(memoized))
        {
            return memoized;
        }

        try
        {
            string resolvedPath = ResolveShortcutTarget(filePath);
            resolvedPath = ResolveKnownFolderGuid(resolvedPath);
            string modernPath = ResolveModernAppRedirect(resolvedPath);
            string keyPath = !string.IsNullOrWhiteSpace(modernPath) ? modernPath : (!string.IsNullOrWhiteSpace(resolvedPath) ? resolvedPath : filePath);
            string hashName = $"v4_{Math.Abs(keyPath.ToLowerInvariant().GetHashCode())}.png";
            string cachedFilePath = Path.Combine(IconCacheDir, hashName);

            // Fast check: if already cached on disk, return existing without opening stream
            if (File.Exists(cachedFilePath))
            {
                try
                {
                    var fi = new FileInfo(cachedFilePath);
                    if (fi.Length > 200)
                    {
                        _iconPathCache[filePath] = cachedFilePath;
                        return cachedFilePath;
                    }
                }
                catch { }
            }

            // Extract best quality icon via multi-tier strategy
            BitmapSource? bs = ExtractBestQualityIcon(filePath, resolvedPath, modernPath);
            if (bs != null)
            {
                // Auto-trim transparent borders and shell backplates so small icons fill the container bold and large
                bs = TrimTransparentPadding(bs);

                using (var fs = new FileStream(cachedFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bs));
                    encoder.Save(fs);
                }
                _iconPathCache[filePath] = cachedFilePath;
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
        if (string.IsNullOrWhiteSpace(shortcutPath)) return shortcutPath;

        string kf = ResolveKnownFolderGuid(shortcutPath);
        if (File.Exists(kf) || Directory.Exists(kf))
        {
            shortcutPath = kf;
        }

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
                    string target = shortcut.TargetPath?.ToString() ?? string.Empty;
                    string iconLoc = shortcut.IconLocation?.ToString() ?? string.Empty;
                    Marshal.FinalReleaseComObject(shortcut);
                    Marshal.FinalReleaseComObject(shell);

                    // Check IconLocation first (MSI advertised shortcuts store their high-res icon path here)
                    if (!string.IsNullOrWhiteSpace(iconLoc))
                    {
                        string iconFile = iconLoc.Split(',')[0].Trim('\"', ' ');
                        string resolvedIconFile = ResolveKnownFolderGuid(iconFile);
                        if (File.Exists(resolvedIconFile))
                        {
                            return resolvedIconFile;
                        }
                        if (File.Exists(iconFile))
                        {
                            return iconFile;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        string resolvedTarget = ResolveKnownFolderGuid(target);
                        if (File.Exists(resolvedTarget) || Directory.Exists(resolvedTarget))
                        {
                            return resolvedTarget;
                        }
                        if (File.Exists(target) || Directory.Exists(target))
                        {
                            return target;
                        }
                    }
                }
            }
        }
        catch { }

        return shortcutPath;
    }
}
