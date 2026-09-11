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
