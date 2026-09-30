using CommunityToolkit.Mvvm.Messaging;

namespace MetroHub.Widgets.Messaging;

/// <summary>
/// Central weak-reference messenger for widget events and hub-to-widget notifications.
/// Uses WeakReferenceMessenger to prevent memory leaks from dangling static subscriptions.
/// </summary>
public static class WidgetMessenger
{
    public static IMessenger Default => WeakReferenceMessenger.Default;

    public static TMessage Send<TMessage>(TMessage message) where TMessage : class
    {
        return WeakReferenceMessenger.Default.Send(message);
    }
}

/// <summary>
/// Dispatched when MetroHub is shown or hidden (ShowScreen/HideScreen).
/// Widgets subscribe to pause/resume background timers or polling.
/// </summary>
public record HubVisibilityChangedMessage(bool IsVisible);

/// <summary>
/// Dispatched when widget settings are modified.
/// </summary>
public record WidgetSettingsChangedMessage(string TileId, string? SettingsJson);
