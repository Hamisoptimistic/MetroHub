using System;

namespace MetroHub.Core.Messaging;

/// <summary>
/// Weak reference message dispatched when an asynchronous application or shell shortcut launch fails.
/// Replaces the legacy static event to decouple interop from UI and prevent memory leaks.
/// </summary>
public record TargetLaunchFailedMessage(string Title, Exception Exception);
