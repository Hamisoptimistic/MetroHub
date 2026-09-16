namespace MetroHub.Core.Display;

/// <summary>
/// Immutable snapshot representing a connected display device and its hardware brightness capabilities.
/// </summary>
public sealed record MonitorDevice
{
    public required string Id { get; init; }
    public required string DeviceName { get; init; }
    public required string FriendlyName { get; init; }
    public required int DisplayIndex { get; init; }
    public required uint CurrentBrightness { get; init; }
    public required uint MinBrightness { get; init; }
    public required uint MaxBrightness { get; init; }
    public required bool IsInternal { get; init; }
    public required bool IsPrimary { get; init; }
    public required bool IsSupported { get; init; }
    public int PhysicalIndex { get; init; } = 0;
    public string? WmiInstanceName { get; init; }
    public required System.Windows.Rect Bounds { get; init; }
}
