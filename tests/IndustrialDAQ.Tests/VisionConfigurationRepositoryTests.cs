using IndustrialDAQ.Infrastructure;
using IndustrialDAQ.Vision.Algorithms;
using IndustrialDAQ.Vision.Models;
using IndustrialDAQ.Vision.Storage;
using Microsoft.EntityFrameworkCore;

namespace IndustrialDAQ.Tests;

public sealed class VisionConfigurationRepositoryTests
{
    [Fact]
    public async Task Repository_RoundTripsCameraTaskAndResult()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "industrialdaq-vision-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<DaqDbContext>().UseSqlite("Data Source=" + databasePath).Options;
        var factory = new TestFactory(options);
        var repository = new VisionConfigurationRepository(factory);

        await repository.UpsertCameraAsync(new VisionCameraConfig
        {
            CameraId = "cap-camera", Name = "瓶盖相机", DriverType = VisionCameraDriverTypes.HikvisionMvs,
            ImageDirectory = "samples", DeviceSerialNumber = "HIK-VIRTUAL-001", DeviceIpAddress = "192.168.1.88",
            IntervalMilliseconds = 500
        });
        await repository.UpsertTaskAsync(new VisionInspectionTask
        {
            TaskId = "cap-task", CameraId = "cap-camera", Name = "瓶盖有无",
            AlgorithmType = VisionOperatorPipelineAlgorithm.TypeName,
            Operators = [VisionOperatorCatalog.Find("Grayscale").CreateDefault(0),
                VisionOperatorCatalog.Find("Brightness").CreateDefault(1),
                VisionOperatorCatalog.Find("TemplateMatch").CreateDefault(2)],
            Roi = new VisionRoi(0.2, 0.2, 0.5, 0.5), MatchThreshold = 0.86,
            TemplateImagePath = "templates/cap.png"
        });
        await repository.SaveResultAsync(new VisionInspectionResult
        {
            RecordId = "record-1", TaskId = "cap-task", CameraId = "cap-camera",
            FrameId = "frame-1", IsPass = true, CapPresent = true, MatchScore = 0.92
        });

        var camera = Assert.Single(await repository.LoadCamerasAsync());
        var task = Assert.Single(await repository.LoadTasksAsync());
        Assert.Equal("samples", camera.ImageDirectory);
        Assert.Equal(VisionCameraDriverTypes.HikvisionMvs, camera.DriverType);
        Assert.Equal("HIK-VIRTUAL-001", camera.DeviceSerialNumber);
        Assert.Equal("192.168.1.88", camera.DeviceIpAddress);
        Assert.Equal(0.86, task.MatchThreshold);
        Assert.Equal(0.2, task.Roi.X);
        Assert.Equal(VisionOperatorPipelineAlgorithm.TypeName, task.AlgorithmType);
        Assert.Equal(["Grayscale", "Brightness", "TemplateMatch"], task.Operators.Select(item => item.OperatorType));
        Assert.Equal("0.35", task.Operators[2].Parameters["X"]);

        await repository.DeleteTaskAsync(task.TaskId);
        Assert.Empty(await repository.LoadTasksAsync());
    }

    private sealed class TestFactory(DbContextOptions<DaqDbContext> options) : IDbContextFactory<DaqDbContext>
    {
        public DaqDbContext CreateDbContext() => new(options);
    }
}
