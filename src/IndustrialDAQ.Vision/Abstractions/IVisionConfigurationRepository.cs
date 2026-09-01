using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Abstractions;

/// <summary>视觉配置与检测记录持久化契约。</summary>
public interface IVisionConfigurationRepository
{
    Task<IReadOnlyList<VisionCameraConfig>> LoadCamerasAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VisionInspectionTask>> LoadTasksAsync(CancellationToken cancellationToken = default);
    Task UpsertCameraAsync(VisionCameraConfig camera, CancellationToken cancellationToken = default);
    Task UpsertTaskAsync(VisionInspectionTask task, CancellationToken cancellationToken = default);
    Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task SaveResultAsync(VisionInspectionResult result, CancellationToken cancellationToken = default);
}
