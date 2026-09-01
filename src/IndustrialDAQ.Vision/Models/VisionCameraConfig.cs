namespace IndustrialDAQ.Vision.Models;

/// <summary>视觉相机配置，MVP 使用目录驱动模拟工业相机。</summary>
public sealed class VisionCameraConfig
{
    public string CameraId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DriverType { get; init; } = "Directory";
    public string ImageDirectory { get; init; } = string.Empty;
    public VisionTriggerMode TriggerMode { get; init; } = VisionTriggerMode.Continuous;
    public int IntervalMilliseconds { get; init; } = 1000;
    public bool Loop { get; init; } = true;
    public bool IsEnabled { get; init; } = true;
}

public enum VisionTriggerMode
{
    Continuous,
    Software
}
