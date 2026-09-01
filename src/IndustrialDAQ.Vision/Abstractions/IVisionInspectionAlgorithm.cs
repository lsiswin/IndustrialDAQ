using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Abstractions;

/// <summary>视觉算法统一契约，运行引擎不依赖具体 OpenCV 或 ONNX 实现。</summary>
public interface IVisionInspectionAlgorithm
{
    string AlgorithmType { get; }
    ValueTask<VisionInspectionResult> InspectAsync(
        VisionFrame frame,
        VisionInspectionTask task,
        CancellationToken cancellationToken = default);
}
