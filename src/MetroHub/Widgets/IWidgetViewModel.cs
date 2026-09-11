using System.Collections.Generic;

namespace MetroHub.Widgets;

/// <summary>
/// Contract for widget ViewModels. Declares supported grid sizes for dynamic context menu generation (Phase 0.3).
/// </summary>
public interface IWidgetViewModel
{
    IReadOnlyList<WidgetSize> AllowedSizes { get; }
}
