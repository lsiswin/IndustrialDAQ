using IndustrialDAQ.Alarm.RuleBuilder;
using IndustrialDAQ.Alarm.RuleEngine;
using IndustrialDAQ.Core;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Infrastructure;
using IndustrialDAQ.Storage;
using IndustrialDAQ.Vision.Algorithms;
using IndustrialDAQ.Vision.Models;
using IndustrialDAQ.Vision.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace IndustrialDAQ.Tests;

/// <summary>验证瓶盖图像、视觉数据点、实时库、历史库和报警规则的完整运行链路。</summary>
public sealed class VisionAlarmFlowTests
{
    [Fact]
    public async Task MissingCap_PublishesRuntimeTags_TriggersAlarm_AndPersistsHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "industrialdaq-vision-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "vision-flow.db");
        var options = new DbContextOptionsBuilder<DaqDbContext>().UseSqlite("Data Source=" + databasePath).Options;
        var factory = new TestFactory(options);
        await using (var database = factory.CreateDbContext()) await database.Database.EnsureCreatedAsync();

        var roi = new VisionRoi(0.25, 0.25, 0.5, 0.5);
        var teacher = new VisionTemplateTeachingService();
        var templatePath = Path.Combine(root, "cap-template.png");
        await teacher.CreateTemplateAsync(
            new VisionFrame("teach", "camera", CreateBottleImage(true), DateTimeOffset.UtcNow), roi, templatePath);
        var task = new VisionInspectionTask
        {
            TaskId = "cap-flow", CameraId = "camera", Name = "瓶盖有无检测",
            Roi = roi, MatchThreshold = 0.75, TemplateImagePath = templatePath
        };
        var result = await new CapPresenceTemplateAlgorithm().InspectAsync(
            new VisionFrame("ng-frame", "camera", CreateBottleImage(false), DateTimeOffset.UtcNow), task);
        Assert.False(result.IsPass);

        var channel = new AcquisitionChannel(64);
        var store = new RealTimeStore();
        var historyWriter = new HistoryWriter(channel, store, factory, NullLogger<HistoryWriter>.Instance);
        historyWriter.RegisterTags(VisionTagCatalog.CreateTagPoints(task));
        var signalBus = new AlarmRuleSignalBus();
        var definitionService = new AlarmDefinitionService(new StubAlarmRepository(new AlarmDefinition
        {
            Id = "alarm-cap-flow", RuleId = "vision-cap-flow", AlarmCode = "VISION_CAP_MISSING",
            TagId = VisionTagCatalog.CapPresent(task.TaskId), TagName = "Result.CapPresent",
            AlarmType = AlarmType.Bool, ConditionExpression = "Value == false", ClearExpression = "Value == true",
            Title = "瓶盖缺失", IsEnabled = true
        }));
        var ruleEngine = new RuleEngineService(
            store, definitionService, new AlarmRuleBuilder(), signalBus, NullLogger<RuleEngineService>.Instance);

        await historyWriter.StartAsync(CancellationToken.None);
        await ruleEngine.StartAsync(CancellationToken.None);
        await new VisionResultPublisher(channel).PublishResultAsync(task, result);

        var signal = await signalBus.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(signal.IsTriggered);
        Assert.False(Assert.IsType<bool>(signal.Value));
        Assert.False(Assert.IsType<bool>(store.TryGetValue(VisionTagCatalog.CapPresent(task.TaskId))!.Value));

        await ruleEngine.StopAsync(CancellationToken.None);
        await historyWriter.StopAsync(CancellationToken.None);
        await using var verification = factory.CreateDbContext();
        Assert.Contains(await verification.HistoricalRecords.ToListAsync(),
            item => item.TagId == VisionTagCatalog.CapPresent(task.TaskId));
    }

    private static byte[] CreateBottleImage(bool hasCap)
    {
        using var image = new Mat(new Size(200, 200), MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(65, 70, 70, 120), new Scalar(70, 70, 70), -1);
        if (hasCap)
        {
            Cv2.Circle(image, new Point(100, 65), 35, new Scalar(230, 230, 230), -1);
            Cv2.Circle(image, new Point(100, 65), 24, new Scalar(120, 120, 120), 4);
        }
        return image.ImEncode(".png");
    }

    private sealed class TestFactory(DbContextOptions<DaqDbContext> options) : IDbContextFactory<DaqDbContext>
    {
        public DaqDbContext CreateDbContext() => new(options);
    }

    private sealed class StubAlarmRepository(params AlarmDefinition[] definitions) : IAlarmDefinitionRepository
    {
        public Task<IReadOnlyList<AlarmDefinition>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AlarmDefinition>>(definitions);
        public Task<AlarmDefinition?> FindByRuleIdAsync(string ruleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(definitions.FirstOrDefault(item => item.RuleId == ruleId));
        public Task UpsertAsync(AlarmDefinition definition, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisableAsync(string ruleId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
