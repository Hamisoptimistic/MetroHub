using System;
using System.Collections.Generic;
using System.Linq;

namespace MetroHub.Core.Services.Catalog;

/// <summary>
/// Central registry and service manager for modular catalog providers in MetroHub.
/// Any new catalog (Widgets, System Tools, Bookmarks, Folders) can be added simply by
/// implementing ICatalogProvider and registering it here, requiring zero changes to UI or menu logic.
/// </summary>
public static class CatalogService
{
    private static readonly List<ICatalogProvider> _providers = new();
    private static readonly object _lock = new();

    static CatalogService()
    {
        // Register default built-in providers
        RegisterProvider(new InstalledAppsCatalogProvider());
    }

    /// <summary>
    /// Registers a new catalog provider into the system.
    /// </summary>
    public static void RegisterProvider(ICatalogProvider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));

        lock (_lock)
        {
            if (!_providers.Any(p => p.Id.Equals(provider.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _providers.Add(provider);
            }
        }
    }

    /// <summary>
    /// Gets all registered catalog providers sorted by their priority order.
    /// </summary>
    public static IReadOnlyList<ICatalogProvider> GetProviders()
    {
        lock (_lock)
        {
            return _providers.OrderBy(p => p.Priority).ToList();
        }
    }

    /// <summary>
    /// Finds a specific provider by identifier.
    /// </summary>
    public static ICatalogProvider? GetProvider(string id)
    {
        lock (_lock)
        {
            return _providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }
}
