using System.Collections.Generic;
using System.Threading.Tasks;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services.Catalog;

/// <summary>
/// Defines a modular contract for a catalog source that supplies items
/// (Apps, Widgets, Tools, Bookmarks, Folders, etc.) to the MetroHub UI.
/// Implementations are registered with CatalogService.
/// </summary>
public interface ICatalogProvider
{
    /// <summary>
    /// Unique identifier for this provider (e.g., "installed_apps", "widgets").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display header shown in the context menu (e.g., "Apps", "Widgets").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Optional Fluent symbol icon glyph or name.
    /// </summary>
    string? IconGlyph { get; }

    /// <summary>
    /// Sort priority for ordering catalog submenus in the context menu. Lower numbers appear first.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Asynchronously retrieves the catalog items provided by this source.
    /// </summary>
    Task<IReadOnlyList<CatalogItemModel>> GetItemsAsync(bool forceRefresh = false);
}
