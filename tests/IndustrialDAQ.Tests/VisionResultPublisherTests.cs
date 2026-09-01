using IndustrialDAQ.Core;
using IndustrialDAQ.Vision.Models;
using IndustrialDAQ.Vision.Runtime;

namespace IndustrialDAQ.Tests;

public sealed class VisionResultPublisherTests
{
    [Fact]
    public async Task Result_IsPublishedAsStableRuntimeTags()
    {
        var channel = new AcquisitionChannel(32);
        var publisher = new VisionResultPublisher(channel);
        var task = new VisionInspectionTask { TaskId = "cap-task", CameraId = "camera", Name = "瓶盖有无" };
        await publisher.PublishResultAsync(task, new VisionInspectionResult
        {
            TaskId = task.TaskId, CameraId = task.CameraId, FrameId = "frame-1",
            IsPass = false, CapPresent = false, MatchScore = 0.31, ProcessingTimeMilliseconds = 12
        });

        var values = new List<IndustrialDAQ.Core.Models.TagValue>();
        for (var index = 0; index < 7; index++) values.Add(await channel.Reader.ReadAsync());
        Assert.False(Assert.IsType<bool>(values.Single(item => item.TagId == VisionTagCatalog.CapPresent(task.TaskId)).Value));
        Assert.Equal(0.31, Assert.IsType<double>(values.Single(item => item.TagId == VisionTagCatalog.MatchScore(task.TaskId)).Value));
        Assert.Equal(1L, Assert.IsType<long>(values.Single(item => item.TagId == VisionTagCatalog.NgCount(task.TaskId)).Value));
    }
}
