using System;

namespace MetroHub.Core.Services;

/// <summary>
/// Dedicated state service holding canonical hub visibility state (Phase 3 T-22).
/// Decouples UI visibility tracking from diagnostic loggers and provides visibility state and events.
/// </summary>
public static class HubState
{
    private static volatile bool _isVisible = true;

    /// <summary>
    /// Gets whether the hub window is currently visible to the user.
    /// </summary>
    public static bool IsVisible => _isVisible;

    /// <summary>
    /// Gets whether the hub window is hidden / dormant.
    /// </summary>
    public static bool IsHidden => !_isVisible;

    /// <summary>
    /// Event raised when hub visibility changes. Passes new IsVisible state.
    /// </summary>
    public static event Action<bool>? VisibilityChanged;

    /// <summary>
    /// Updates the canonical hub visibility state and notifies listeners.
    /// </summary>
    public static void SetVisibility(bool isVisible)
    {
        if (_isVisible == isVisible) return;
        _isVisible = isVisible;
        Safe.Try(() => VisibilityChanged?.Invoke(isVisible), context: "HubState.VisibilityChanged");
    }
}
