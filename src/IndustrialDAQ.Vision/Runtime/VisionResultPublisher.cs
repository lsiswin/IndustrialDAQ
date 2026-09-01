using System.Collections.Concurrent;
using IndustrialDAQ.Core;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Runtime;

/// <summary>将视觉状态和结果写入统一采集管道，复用历史、实时值、规则与报警链路。</summary>
public sealed class VisionResultPublisher
{
    private readonly AcquisitionChannel _channel;
    private readonly ConcurrentDictionary<string, VisionStatistics> _statistics = new(StringComparer.OrdinalIgnoreCase);

    public VisionResultPublisher(AcquisitionChannel channel) => _channel = channel;

    /// <summary>清零指定任务的运行统计，不删除历史检测记录。</summary>
    public void ResetStatistics(string taskId) => _statistics.TryRemove(taskId, out _);

    public async Task PublishStatusAsync(VisionInspectionTask task, bool connected, bool running, CancellationToken cancellationToken = default)
    {
        await PublishAsync(VisionTagCatalog.Connected(task.TaskId), "Camera.Connected", connected, cancellationToken);
        await PublishAsync(VisionTagCatalog.Running(task.TaskId), "Task.Running", running, cancellationToken);
    }

    public async Task PublishResultAsync(VisionInspectionTask task, VisionInspectionResult result, CancellationToken cancellationToken = default)
    {
        var statistics = _statistics.AddOrUpdate(task.TaskId,
            _ => new VisionStatistics(1, result.IsPass ? 0 : 1),
            (_, current) => new VisionStatistics(current.Total + 1, current.Ng + (result.IsPass ? 0 : 1)));
        var passRate = statistics.Total == 0 ? 0 : (statistics.Total - statistics.Ng) * 100d / statistics.Total;

        await PublishAsync(VisionTagCatalog.Pass(task.TaskId), "Result.Pass", result.IsPass, cancellationToken, result.Timestamp);
        await PublishAsync(VisionTagCatalog.CapPresent(task.TaskId), "Result.CapPresent", result.CapPresent, cancellationToken, result.Timestamp);
        await PublishAsync(VisionTagCatalog.MatchScore(task.TaskId), "Result.MatchScore", result.MatchScore, cancellationToken, result.Timestamp);
        await PublishAsync(VisionTagCatalog.ProcessingTime(task.TaskId), "Result.ProcessingTime", result.ProcessingTimeMilliseconds, cancellationToken, result.Timestamp);
        await PublishAsync(VisionTagCatalog.TotalCount(task.TaskId), "Statistics.TotalCount", statistics.Total, cancellationToken, result.Timestamp);
        await PublishAsync(VisionTagCatalog.NgCount(task.TaskId), "Statistics.NgCount", statistics.Ng, cancellationToken, result.Timestamp);
        await PublishAsync(VisionTagCatalog.PassRate(task.TaskId), "Statistics.PassRate", passRate, cancellationToken, result.Timestamp);
    }

    private ValueTask PublishAsync(
        string tagId, string tagName, object value, CancellationToken cancellationToken, DateTimeOffset? timestamp = null) =>
        _channel.Writer.WriteAsync(new TagValue
        {
            TagId = tagId, TagName = tagName, Value = value, Quality = Quality.Good,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow, DataType = value.GetType()
        }, cancellationToken);

    private sealed record VisionStatistics(long Total, long Ng);
}
