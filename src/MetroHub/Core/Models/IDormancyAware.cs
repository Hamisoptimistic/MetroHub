namespace MetroHub.Core.Models;

/// <summary>
/// Implemented by widget ViewModels or Views that need to pause/resume high-frequency
/// render loops, animations, or UI polling when switching between workspaces.
/// Stateful background services (timers, audio streams, network monitors) continue running.
/// </summary>
public interface IDormancyAware
{
    void OnDormant();
    void OnAwakened();
}
