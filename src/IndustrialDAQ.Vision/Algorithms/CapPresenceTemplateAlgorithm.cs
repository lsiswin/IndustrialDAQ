using System.Diagnostics;
using IndustrialDAQ.Vision.Abstractions;
using IndustrialDAQ.Vision.Models;
using OpenCvSharp;

namespace IndustrialDAQ.Vision.Algorithms;

/// <summary>固定工位瓶盖有无检测：对 ROI 灰度归一化后执行归一化模板匹配。</summary>
public sealed class CapPresenceTemplateAlgorithm : IVisionInspectionAlgorithm
{
    public string AlgorithmType => "CapPresenceTemplate";

    public ValueTask<VisionInspectionResult> InspectAsync(
        VisionFrame frame,
        VisionInspectionTask task,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!File.Exists(task.TemplateImagePath))
                throw new FileNotFoundException("瓶盖合格模板不存在。", task.TemplateImagePath);

            using var source = VisionImageProcessor.Decode(frame.EncodedImage);
            using var search = VisionImageProcessor.Prepare(source, task.Roi);
            using var template = Cv2.ImRead(task.TemplateImagePath, ImreadModes.Grayscale);
            if (template.Empty()) throw new InvalidOperationException("瓶盖模板读取失败。");
            Cv2.GaussianBlur(template, template, new Size(5, 5), 0);
            Cv2.EqualizeHist(template, template);

            using var resizedTemplate = ResizeTemplateIfNeeded(template, search.Size());
            using var response = new Mat();
            Cv2.MatchTemplate(search, resizedTemplate, response, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(response, out _, out var score, out _, out _);
            if (double.IsNaN(score) || double.IsInfinity(score)) score = 0;
            score = Math.Clamp(score, 0, 1);
            var present = score >= task.MatchThreshold;

            return ValueTask.FromResult(CreateResult(frame, task, present, score, stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(CreateResult(frame, task, false, 0, stopwatch.Elapsed.TotalMilliseconds, ex.Message));
        }
    }

    private static Mat ResizeTemplateIfNeeded(Mat template, Size searchSize)
    {
        if (template.Width <= searchSize.Width && template.Height <= searchSize.Height) return template.Clone();
        var scale = Math.Min((double)searchSize.Width / template.Width, (double)searchSize.Height / template.Height);
        var resized = new Mat();
        Cv2.Resize(template, resized, new Size(Math.Max(1, (int)(template.Width * scale)), Math.Max(1, (int)(template.Height * scale))));
        return resized;
    }

    private static VisionInspectionResult CreateResult(
        VisionFrame frame, VisionInspectionTask task, bool present, double score, double elapsed, string? error = null) => new()
    {
        TaskId = task.TaskId, CameraId = frame.CameraId, FrameId = frame.FrameId,
        Timestamp = frame.Timestamp, IsPass = present && string.IsNullOrWhiteSpace(error),
        CapPresent = present, MatchScore = score, ProcessingTimeMilliseconds = elapsed,
        FailureReason = error ?? (present ? string.Empty : $"瓶盖匹配分数 {score:F3} 低于阈值 {task.MatchThreshold:F3}")
    };
}
