using System.Collections.Generic;
using MetroHub.Core.Models;

namespace MetroHub.Widgets;

/// <summary>
/// Contract for widget ViewModels.
/// Declares supported grid sizes and lifecycle hooks for state boundary adaptation and power conservation.
/// </summary>
public interface IWidgetViewModel
{
    TileModel Model { get; }
    IReadOnlyList<WidgetSize> AllowedSizes { get; }
    void Initialize(TileModel model);
    void SaveSettings();
    void Pause();
    void Resume();
}

