using System;
using System.Diagnostics;
using System.IO;
using MetroHub.Core.Models;
using Microsoft.Win32;

namespace MetroHub.Core.Services;

/// <summary>
/// Service providing direct application uninstallation with fallback to Windows Settings.
/// </summary>
public static class AppUninstallService
{
    public static void RequestUninstall(CatalogItemModel item)
    {
        try
        {
            // 1. Attempt to find and launch native uninstaller string from Registry
            string? uninstallCmd = FindUninstallString(item.Name, item.TargetPath);
            if (!string.IsNullOrWhiteSpace(uninstallCmd))
            {
                if (LaunchUninstallString(uninstallCmd))
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "AppUninstallService.LaunchUninstallString");
        }

        // 2. Fallback to Windows 11 Installed Apps Settings
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "AppUninstallService.LaunchSettingsAppsFeatures");
            try
            {
                Process.Start(new ProcessStartInfo("appwiz.cpl") { UseShellExecute = true });
            }
            catch (Exception innerEx)
            {
                Safe.Log(innerEx, "AppUninstallService.LaunchAppwizCpl");
            }
        }
    }

    private static string? FindUninstallString(string appName, string? targetPath)
    {
        string[] registryRoots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var rootKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var subPath in registryRoots)
            {
                try
                {
                    using var key = rootKey.OpenSubKey(subPath);
                    if (key == null) continue;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = key.OpenSubKey(subKeyName);
                            if (subKey == null) continue;

                            string? displayName = subKey.GetValue("DisplayName") as string;
                            string? uninstallString = subKey.GetValue("UninstallString") as string;
                            string? installLocation = subKey.GetValue("InstallLocation") as string;

                            if (string.IsNullOrWhiteSpace(uninstallString)) continue;

                            if (!string.IsNullOrWhiteSpace(displayName) &&
                                (string.Equals(displayName, appName, StringComparison.OrdinalIgnoreCase) ||
                                 displayName.Contains(appName, StringComparison.OrdinalIgnoreCase) ||
                                 appName.Contains(displayName, StringComparison.OrdinalIgnoreCase)))
                            {
                                return uninstallString;
                            }

                            if (!string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(installLocation) &&
                                targetPath.StartsWith(installLocation, StringComparison.OrdinalIgnoreCase))
                            {
                                return uninstallString;
                            }
                        }
                        catch (Exception ex)
                        {
                            Safe.Ignore(ex, "AppUninstallService.OpenSubKey");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Safe.Ignore(ex, "AppUninstallService.RegistryRoot");
                }
            }
        }

        return null;
    }

    private static bool LaunchUninstallString(string cmd)
    {
        cmd = cmd.Trim();
        string fileName;
        string args = string.Empty;

        if (cmd.StartsWith('"'))
        {
            int quoteEnd = cmd.IndexOf('\"', 1);
            if (quoteEnd > 0)
            {
                fileName = cmd[1..quoteEnd];
                args = cmd[(quoteEnd + 1)..].Trim();
            }
            else
            {
                fileName = cmd.Trim('\"');
            }
        }
        else
        {
            int spaceIdx = cmd.IndexOf(' ');
            if (spaceIdx > 0 && !File.Exists(cmd))
            {
                fileName = cmd[..spaceIdx];
                args = cmd[(spaceIdx + 1)..].Trim();
            }
            else
            {
                fileName = cmd;
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = true
        };

        Process.Start(psi);
        return true;
    }
}
