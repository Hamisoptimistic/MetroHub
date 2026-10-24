namespace MetroHub.Core.AsciiArt;

/// <summary>
/// One frame of musical interpretation handed to the live halftone renderer:
/// smoothed band energies, a beat-locked pulse, and an idle blend used to keep
/// the wallpaper breathing gently when the radio is quiet.
/// </summary>
public readonly record struct ReactiveState(
    float Bass,
    float Mid,
    float Treble,
    float Pulse,
    float Energy,
    float Breath,
    float Idle,
    bool IsSilent)
{
    /// <summary>A perfectly still frame, used for static previews and tests.</summary>
    public static readonly ReactiveState Rest = new(0f, 0f, 0f, 0f, 0f, 0f, 1f, true);
}
