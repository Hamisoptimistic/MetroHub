using System;

namespace MetroHub.Presentation.Controls;

public sealed class WebLinkCreatedEventArgs : EventArgs
{
    public string Url { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? IconPath { get; init; }
    public bool AddToCanvas { get; init; }
    public bool AddToSidebar { get; init; }
}
