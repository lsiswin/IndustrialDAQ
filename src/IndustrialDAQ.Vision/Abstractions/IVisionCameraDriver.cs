using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Abstractions;

/// <summary>工业相机统一驱动接口，目录、GigE 和厂商 SDK 均实现该契约。</summary>
public interface IVisionCameraDriver : IAsyncDisposable
{
    string CameraId { get; }
    bool IsConnected { get; }
    IAsyncEnumerable<VisionFrame> CaptureAsync(CancellationToken cancellationToken = default);
    ValueTask<VisionFrame?> TriggerAsync(CancellationToken cancellationToken = default);
}
