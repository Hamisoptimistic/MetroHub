using System;
using System.Windows.Media;

namespace MetroHub.Core.Audio;

public class AudioDeviceModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "\uE7F5"; // Default speaker glyph (\uE7F5 = Volume3, \uE7F6 = Headphones)
    public bool IsDefault { get; set; }

    public override string ToString() => $"{Name} (Default: {IsDefault})";
}

public class AppAudioSessionModel
{
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ImageSource? IconSource { get; set; }
    public float Volume { get; set; } = 1.0f; // 0.0 to 1.0
    public int VolumePercent => (int)Math.Round(Volume * 100f);
    public bool IsMuted { get; set; }
    public bool IsSystemSounds { get; set; }

    public override string ToString() => $"{DisplayName} (PID: {ProcessId}, Vol: {VolumePercent}%, Muted: {IsMuted})";
}
