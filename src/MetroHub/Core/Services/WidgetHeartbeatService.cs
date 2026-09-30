using System;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Widgets.Messaging;

namespace MetroHub.Core.Services;

/// <summary>
/// Central 1-second heartbeat service for all MetroHub widgets.
/// Eliminates timer sprawl across widgets, enabling true 0.0% CPU sleep states.
/// Automatically pauses when MetroHub is hidden and resumes when shown.
/// </summary>
public static class WidgetHeartbeatService
{
    private static DispatcherTimer? _timer;
    private static bool _isHubVisible = true;

    public static event Action<DateTime>? SecondTick;
    internal static int SubscriberCount => SecondTick?.GetInvocationList().Length ?? 0;

    static WidgetHeartbeatService()
    {
        // Auto-pause when hub is hidden, resume when shown
        WidgetMessenger.Default.Register<HubVisibilityChangedMessage>(
            WeakReferenceMessenger.Default,
            (r, msg) => SetHubVisibility(msg.IsVisible));
    }

    private static void EnsureTimerInitialized()
    {
        if (_timer != null) return;

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher != null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += OnTick;
        }
    }

    public static void Start()
    {
        _isHubVisible = true;
        EnsureTimerInitialized();
        if (_timer != null && !_timer.IsEnabled)
        {
            _timer.Start();
            // Immediate pulse on resume so widgets refresh instantly
            SecondTick?.Invoke(DateTime.UtcNow);
        }
    }

    public static void Stop()
    {
        _isHubVisible = false;
        if (_timer != null && _timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    public static void SetHubVisibility(bool isVisible)
    {
        if (isVisible)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    /// <summary>
    /// Explicit pulse helper for testing or immediate UI refreshes.
    /// </summary>
    public static void Pulse(DateTime? time = null)
    {
        SecondTick?.Invoke(time ?? DateTime.UtcNow);
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        if (!_isHubVisible) return;
        SecondTick?.Invoke(DateTime.UtcNow);
    }
}
