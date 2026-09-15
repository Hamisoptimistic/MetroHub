using System;

namespace MetroHub.Core.Network;

public enum SpeedTestPhase
{
    Idle,
    Connecting,
    Ping,
    Download,
    Upload,
    Completed,
    Failed,
    Cancelled
}

public class SpeedTestProgress
{
    public SpeedTestPhase Phase { get; init; }
    public double InstantaneousMbps { get; init; }
    public double PeakMbps { get; init; }
    public double PhaseProgress { get; init; } // 0.0 to 1.0 within phase
    public double? PingMs { get; init; }
    public double? JitterMs { get; init; }
    public double? FinalDownloadMbps { get; init; }
    public double? FinalUploadMbps { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
    public string? ServerLocation { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsMeteredConnection { get; init; }
}
