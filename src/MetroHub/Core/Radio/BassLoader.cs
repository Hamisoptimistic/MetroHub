using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using ManagedBass;

namespace MetroHub.Core.Radio;

/// <summary>
/// Bulletproof modern .NET 10 dynamic native library resolver for Un4seen BASS audio engine binaries.
/// Resolves 64-bit bass.dll and bass_aac.dll across runtime base directories, RID folders, and test runners.
/// </summary>
public static class BassLoader
{
    private static int _registered;

    /// <summary>
    /// Registers the native P/Invoke resolver for the ManagedBass assembly. Idempotent and thread-safe.
    /// </summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        try
        {
            var bassAssembly = typeof(Bass).Assembly;

            NativeLibrary.SetDllImportResolver(bassAssembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName.Equals("bass", StringComparison.OrdinalIgnoreCase) ||
                    libraryName.Equals("bass_aac", StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        ? libraryName
                        : $"{libraryName}.dll";

                    // 1. Direct application directory (AppContext.BaseDirectory)
                    string baseDir = AppContext.BaseDirectory;
                    string directPath = Path.Combine(baseDir, fileName);
                    if (File.Exists(directPath))
                    {
                        if (NativeLibrary.TryLoad(directPath, out IntPtr handle))
                        {
                            return handle;
                        }
                    }

                    // 2. Standard .NET RID folder (runtimes/win-x64/native/)
                    string ridPath = Path.Combine(baseDir, "runtimes", "win-x64", "native", fileName);
                    if (File.Exists(ridPath))
                    {
                        if (NativeLibrary.TryLoad(ridPath, out IntPtr handle))
                        {
                            return handle;
                        }
                    }

                    // 3. Solution lib/native directory fallback (useful in tests/dev)
                    string fallbackPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "lib", "native", "win-x64", fileName));
                    if (File.Exists(fallbackPath))
                    {
                        if (NativeLibrary.TryLoad(fallbackPath, out IntPtr handle))
                        {
                            return handle;
                        }
                    }
                }

                // Default runtime resolution
                return IntPtr.Zero;
            });

            Debug.WriteLine("[BassLoader] Successfully registered native library resolver for ManagedBass.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BassLoader] Failed to register DllImportResolver: {ex.Message}");
        }
    }
}
