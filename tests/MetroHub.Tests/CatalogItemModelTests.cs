using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

[Collection("StorageTests")]
public sealed class CatalogItemModelTests : IDisposable
{
    private readonly string _sandboxDir;

    public CatalogItemModelTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), "MetroHub_TestSandbox_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandboxDir);
        AppPaths.CustomAppDataDir = _sandboxDir;
        StorageService.ResetPending();
        InstalledAppsService.Shutdown();
    }

    public void Dispose()
    {
        try
        {
            StorageService.Flush();
            StorageService.ResetPending();
            InstalledAppsService.Shutdown();
        }
        catch { }
        finally
        {
            AppPaths.CustomAppDataDir = null;
            try
            {
                if (Directory.Exists(_sandboxDir))
                {
                    Directory.Delete(_sandboxDir, recursive: true);
                }
            }
            catch { }
        }
    }

    [Fact]
    public void IconPath_GetterSetterAndPropertyChanged_FiresCorrectly()
    {
        var model = new CatalogItemModel();
        string? changedProperty = null;
        model.PropertyChanged += (s, e) => changedProperty = e.PropertyName;

        model.IconPath = @"C:\cache\icons\v5_test.png";

        Assert.Equal(@"C:\cache\icons\v5_test.png", model.IconPath);
        Assert.Equal(nameof(CatalogItemModel.IconPath), changedProperty);
    }

    [Fact]
    public void StorageService_AppsCache_PersistsAndRestoresIconPath()
    {
        var testApps = new List<CatalogItemModel>
        {
            new()
            {
                Name = "Test App 1",
                TargetPath = @"C:\Apps\TestApp1.exe",
                IconPath = @"C:\cache\icons\v5_app1.png"
            },
            new()
            {
                Name = "Test App 2",
                TargetPath = @"C:\Apps\TestApp2.exe",
                IconPath = @"C:\cache\icons\v5_app2.png"
            }
        };

        StorageService.SaveAppsCache(testApps);
        var loaded = StorageService.LoadAppsCache();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(@"C:\cache\icons\v5_app1.png", loaded[0].IconPath);
        Assert.Equal(@"C:\cache\icons\v5_app2.png", loaded[1].IconPath);
        Assert.Equal("Test App 1", loaded[0].Name);
        Assert.Equal("Test App 2", loaded[1].Name);
    }

    [Fact]
    public void FastPath_PrewarmMemoryCache_UsesIconPath_WhenFileExists()
    {
        WpfTestHost.RunSta(() =>
        {
            // Create a small dummy 1x1 png image file
            string tempPng = Path.Combine(_sandboxDir, "dummy_icon.png");
            byte[] dummyPngBytes = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
                0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
                0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
                0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
                0x42, 0x60, 0x82
            };
            File.WriteAllBytes(tempPng, dummyPngBytes);

            CatalogItemModel.ClearMemoryCache();
            var item = new CatalogItemModel
            {
                Name = "FastPath App",
                TargetPath = @"C:\Fake\FastPathApp.exe",
                IconPath = tempPng
            };

            CatalogItemModel.PrewarmMemoryCache(new[] { item });

            // Allow background task to process
            bool cached = false;
            for (int i = 0; i < 40; i++)
            {
                if (CatalogItemModel.HasMemoryCachedIcon(item.TargetPath))
                {
                    cached = true;
                    break;
                }
                Thread.Sleep(50);
            }

            Assert.True(cached, "Expected target path icon to be placed into memory cache via fast path");
        });
    }

    [Fact]
    public void InstalledAppsService_PreservesIconPath_FromDiskCache()
    {
        var cached = new List<CatalogItemModel>
        {
            new()
            {
                Name = "Cached App",
                TargetPath = @"C:\Apps\cached.exe",
                IconPath = @"C:\cache\icons\v5_cached.png"
            }
        };

        StorageService.SaveAppsCache(cached);

        var apps = InstalledAppsService.GetInstalledApps(forceRefresh: false);

        Assert.NotNull(apps);
        Assert.Contains(apps, a => a.Name == "Cached App" && a.IconPath == @"C:\cache\icons\v5_cached.png");
    }

    [Fact]
    public void Icon_Getter_FastPath_LoadsImageDirectly_WhenIconPathExists()
    {
        WpfTestHost.RunSta(() =>
        {
            string tempPng = Path.Combine(_sandboxDir, "dummy_icon_getter.png");
            byte[] dummyPngBytes = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
                0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
                0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
                0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
                0x42, 0x60, 0x82
            };
            File.WriteAllBytes(tempPng, dummyPngBytes);

            CatalogItemModel.ClearMemoryCache();
            var item = new CatalogItemModel
            {
                Name = "Getter App",
                TargetPath = @"C:\Fake\GetterApp.exe",
                IconPath = tempPng
            };

            // Trigger Icon property getter
            var icon = item.Icon;

            // Wait for background load to complete
            bool loaded = false;
            for (int i = 0; i < 40; i++)
            {
                if (item.Icon != null || CatalogItemModel.HasMemoryCachedIcon(item.TargetPath))
                {
                    loaded = true;
                    break;
                }
                Thread.Sleep(50);
            }

            Assert.True(loaded, "Expected Icon to be populated from IconPath");
            Assert.NotNull(item.Icon);
        });
    }

    [Fact]
    public void CacheMiss_PrewarmMemoryCache_CallsExtractor_AndUpdatesIconPath()
    {
        WpfTestHost.RunSta(() =>
        {
            CatalogItemModel.ClearMemoryCache();
            string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Assert.True(File.Exists(cmdPath), "cmd.exe should exist on Windows");

            var item = new CatalogItemModel
            {
                Name = "Command Prompt",
                TargetPath = cmdPath,
                IconPath = null // simulate cold start cache miss
            };

            CatalogItemModel.PrewarmMemoryCache(new[] { item });

            bool populated = false;
            for (int i = 0; i < 60; i++)
            {
                if (!string.IsNullOrWhiteSpace(item.IconPath) && File.Exists(item.IconPath))
                {
                    populated = true;
                    break;
                }
                Thread.Sleep(50);
            }

            Assert.True(populated, "Expected IconPath to be populated from IconExtractorService on cache miss");
            Assert.EndsWith(".png", item.IconPath, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void InstalledAppsService_PreservesIconPath_OnFreshScan_WhenMatchingTargetPath()
    {
        // First get current apps to find a valid shell app on the system
        var initial = InstalledAppsService.GetInstalledApps(forceRefresh: true);
        if (initial == null || initial.Count == 0) return;

        var targetApp = initial.Find(a => !string.IsNullOrWhiteSpace(a.TargetPath));
        if (targetApp == null) return;

        string expectedIcon = @"C:\custom_cache\icons\v5_preserved.png";
        targetApp.IconPath = expectedIcon;

        // Force a fresh scan (which clears and rebuilds freshApps from shell:AppsFolder)
        var refreshed = InstalledAppsService.GetInstalledApps(forceRefresh: true);
        Assert.NotNull(refreshed);

        var matched = refreshed.Find(a => string.Equals(a.TargetPath, targetApp.TargetPath, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(matched);
        Assert.Equal(expectedIcon, matched.IconPath);
    }

    [Fact]
    public void MemoryIconCache_HoldsMoreThan64ItemsWithoutEviction()
    {
        WpfTestHost.RunSta(() =>
        {
            string tempPng = Path.Combine(_sandboxDir, "dummy_icon_capacity.png");
            byte[] dummyPngBytes = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
                0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
                0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
                0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
                0x42, 0x60, 0x82
            };
            File.WriteAllBytes(tempPng, dummyPngBytes);

            CatalogItemModel.ClearMemoryCache();

            // Prewarm 80 items (previously MaxCachedIcons was 64)
            var items = new List<CatalogItemModel>();
            for (int i = 0; i < 80; i++)
            {
                items.Add(new CatalogItemModel
                {
                    Name = $"App {i}",
                    TargetPath = $@"C:\Fake\App_{i}.exe",
                    IconPath = tempPng
                });
            }

            CatalogItemModel.PrewarmMemoryCache(items, maxItems: 80);

            // Wait for all 80 items to be loaded into memory cache
            int cachedCount = 0;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                cachedCount = 0;
                foreach (var itm in items)
                {
                    if (CatalogItemModel.HasMemoryCachedIcon(itm.TargetPath))
                    {
                        cachedCount++;
                    }
                }
                if (cachedCount >= 80) break;
                Thread.Sleep(50);
            }

            Assert.Equal(80, cachedCount);
            // Verify the very first item was NOT evicted
            Assert.True(CatalogItemModel.HasMemoryCachedIcon(items[0].TargetPath));
        });
    }
}


