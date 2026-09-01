namespace IndustrialDAQ.Vision.Models;

/// <summary>相机输出的不可变图像帧，图像使用编码字节避免跨模块持有原生内存。</summary>
public sealed record VisionFrame(
    string FrameId,
    string CameraId,
    byte[] EncodedImage,
    DateTimeOffset Timestamp,
    string? SourcePath = null);
