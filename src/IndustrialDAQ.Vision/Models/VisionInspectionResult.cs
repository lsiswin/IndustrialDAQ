namespace IndustrialDAQ.Vision.Models;

/// <summary>单帧瓶盖检测结果。</summary>
public sealed class VisionInspectionResult
{
    public string RecordId { get; init; } = Guid.NewGuid().ToString("N");
    public string TaskId { get; init; } = string.Empty;
    public string CameraId { get; init; } = string.Empty;
    public string FrameId { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool IsPass { get; init; }
    public bool CapPresent { get; init; }
    public double MatchScore { get; init; }
    public double ProcessingTimeMilliseconds { get; init; }
    public string? ImagePath { get; init; }
    public string FailureReason { get; init; } = string.Empty;
}

public enum VisionTaskStatus
{
    Stopped,
    Running,
    Paused,
    CameraOffline,
    Faulted
}
