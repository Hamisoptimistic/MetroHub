using System.Collections.Generic;
using System.Threading.Tasks;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services.Catalog;

/// <summary>
/// Catalog provider that lists all installed Windows applications (Win32 and Modern UWP/MSIX),
/// filtered for junk/uninstallers, sorted alphabetically, with lazy non-blocking icon generation.
/// </summary>
public class InstalledAppsCatalogProvider : ICatalogProvider
{
    public string Id => "installed_apps";

    public string DisplayName => "Apps";

    public string? IconGlyph => "Apps24";

    public int Priority => 10;

    public async Task<IReadOnlyList<CatalogItemModel>> GetItemsAsync(bool forceRefresh = false)
    {
        var apps = await InstalledAppsService.GetInstalledAppsAsync(forceRefresh);
        foreach (var app in apps)
        {
            app.ProviderId = Id;
            app.SpanX = 2; // Medium 2x2 by default per user requirement
            app.SpanY = 2;
        }
        return apps;
    }
}
