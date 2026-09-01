namespace IndustrialDAQ.Vision.Models;

/// <summary>视觉相机配置，统一描述目录模拟相机与海康 MVS 相机。</summary>
public sealed class VisionCameraConfig
{
    public string CameraId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DriverType { get; init; } = "Directory";
    public string ImageDirectory { get; init; } = string.Empty;
    public string DeviceSerialNumber { get; init; } = string.Empty;
    public string DeviceIpAddress { get; init; } = string.Empty;
    public VisionTriggerMode TriggerMode { get; init; } = VisionTriggerMode.Continuous;
    public int IntervalMilliseconds { get; init; } = 1000;
    public bool Loop { get; init; } = true;
    public bool IsEnabled { get; init; } = true;
}

/// <summary>视觉相机驱动类型常量，避免业务层散落字符串。</summary>
public static class VisionCameraDriverTypes
{
    public const string Directory = "Directory";
    public const string HikvisionSimulator = "HikvisionSimulator";
    public const string HikvisionMvs = "HikvisionMvs";
}

public enum VisionTriggerMode
{
    Continuous,
    Software
}
