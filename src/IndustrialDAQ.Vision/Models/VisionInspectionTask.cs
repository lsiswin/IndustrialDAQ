namespace IndustrialDAQ.Vision.Models;

/// <summary>瓶盖检测任务，所有检测参数均由配置驱动。</summary>
public sealed class VisionInspectionTask
{
    public string TaskId { get; init; } = string.Empty;
    public string CameraId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string AlgorithmType { get; init; } = "CapPresenceTemplate";
    public IReadOnlyList<VisionOperatorDefinition> Operators { get; init; } = [];
    public VisionRoi Roi { get; init; } = VisionRoi.FullFrame;
    public double MatchThreshold { get; init; } = 0.80;
    public string TemplateImagePath { get; init; } = string.Empty;
    public bool SaveNgImage { get; init; } = true;
    public bool IsEnabled { get; init; } = true;
    public long Version { get; init; } = 1;
}

/// <summary>使用 0 到 1 的归一化坐标，保证更换图像分辨率后 ROI 仍然有效。</summary>
public sealed record VisionRoi(double X, double Y, double Width, double Height)
{
    public static VisionRoi FullFrame { get; } = new(0, 0, 1, 1);

    public bool IsValid => X >= 0 && Y >= 0 && Width > 0 && Height > 0 &&
                           X + Width <= 1 && Y + Height <= 1;
}
