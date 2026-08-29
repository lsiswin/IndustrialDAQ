using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Processing;
using IndustrialDAQ.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndustrialDAQ.Tests;

public sealed class CalculationRuleTests
{
    [Fact]
    public async Task Processor_EvaluatesPersistedStyleRuleIntoVirtualTag()
    {
        var store = new RealTimeStore();
        var processor = new DataProcessor(store, NullLogger<DataProcessor>.Instance);
        processor.ReplaceRules([new CalculationRule { RuleId = "sum", InputTagNames = ["A", "B"], Expression = "A + B", TargetTagId = "virtual-sum", TargetTagName = "Sum", TargetDataType = TagDataType.Float64 }]);
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(30);

        store.Update(new TagValue { TagId = "a", TagName = "A", Value = 2d, Quality = Quality.Good, DataType = typeof(double) });
        store.Update(new TagValue { TagId = "b", TagName = "B", Value = 3d, Quality = Quality.Good, DataType = typeof(double) });

        TagValue? result = null;
        for (var index = 0; index < 20 && result is null; index++) { await Task.Delay(25); result = store.TryGetValue("virtual-sum"); }
        Assert.Equal(5d, Assert.IsType<double>(result?.Value));
        await processor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Processor_UsesTagIdWhenDifferentDevicesHaveSameTagName()
    {
        var store = new RealTimeStore();
        var processor = new DataProcessor(store, NullLogger<DataProcessor>.Instance);
        processor.ReplaceRules([new CalculationRule
        {
            RuleId = "difference",
            Inputs =
            [
                new CalculationInputBinding { Alias = "A", TagId = "device-a-level", TagName = "Level" },
                new CalculationInputBinding { Alias = "B", TagId = "device-b-level", TagName = "Level" }
            ],
            Expression = "A - B",
            TargetTagId = "level-difference",
            TargetTagName = "液位差",
            TargetDataType = TagDataType.Float64
        }]);
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(30);

        store.Update(new TagValue { TagId = "device-a-level", TagName = "Level", Value = 500d, Quality = Quality.Good, DataType = typeof(double) });
        store.Update(new TagValue { TagId = "device-b-level", TagName = "Level", Value = 120d, Quality = Quality.Good, DataType = typeof(double) });

        TagValue? result = null;
        for (var index = 0; index < 20 && result is null; index++) { await Task.Delay(25); result = store.TryGetValue("level-difference"); }
        Assert.Equal(380d, Assert.IsType<double>(result?.Value));
        await processor.StopAsync(CancellationToken.None);
    }
}
