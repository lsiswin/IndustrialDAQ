using IndustrialDAQ.Alarm.RuleBuilder;
using IndustrialDAQ.Alarm.RuleEngine;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Processing;
using IndustrialDAQ.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndustrialDAQ.Tests;

public sealed class CalculationAlarmFlowTests
{
    [Fact]
    public async Task Inputs_AreCalculated_AndVirtualValueTriggersAlarmRule()
    {
        var store = new RealTimeStore();
        var calculation = new CalculationRule
        {
            RuleId = "calc-average",
            Inputs =
            [
                new CalculationInputBinding { Alias = "A", TagId = "level-a", TagName = "液位 A" },
                new CalculationInputBinding { Alias = "B", TagId = "level-b", TagName = "液位 B" }
            ],
            Expression = "(A + B) / 2",
            TargetTagId = "virtual-calc-average",
            TargetTagName = "平均液位"
        };
        var alarm = new AlarmDefinition
        {
            RuleId = "alarm-average-high",
            AlarmCode = "AVERAGE_LEVEL_HIGH",
            TagId = calculation.TargetTagId,
            TagName = calculation.TargetTagName,
            Operator = AlarmOperator.GreaterThan,
            Threshold = 100,
            Title = "平均液位过高",
            IsEnabled = true
        };

        var signalBus = new AlarmRuleSignalBus();
        var definitionService = new AlarmDefinitionService(new StubAlarmRepository(alarm));
        var ruleEngine = new RuleEngineService(store, definitionService, new AlarmRuleBuilder(), signalBus, NullLogger<RuleEngineService>.Instance);
        var processor = new DataProcessor(store, NullLogger<DataProcessor>.Instance);
        processor.ReplaceRules([calculation]);
        await ruleEngine.StartAsync(CancellationToken.None);
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(40);

        store.Update(new TagValue { TagId = "level-a", TagName = "液位", Value = 80d, Quality = Quality.Good, DataType = typeof(double) });
        store.Update(new TagValue { TagId = "level-b", TagName = "液位", Value = 160d, Quality = Quality.Good, DataType = typeof(double) });

        var signal = await signalBus.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("virtual-calc-average", signal.TagId);
        Assert.Equal(120d, Assert.IsType<double>(signal.Value));
        Assert.True(signal.IsTriggered);

        await processor.StopAsync(CancellationToken.None);
        await ruleEngine.StopAsync(CancellationToken.None);
    }

    private sealed class StubAlarmRepository(params AlarmDefinition[] definitions) : IAlarmDefinitionRepository
    {
        public Task<IReadOnlyList<AlarmDefinition>> LoadAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AlarmDefinition>>(definitions);
        public Task<AlarmDefinition?> FindByRuleIdAsync(string ruleId, CancellationToken cancellationToken = default) => Task.FromResult(definitions.FirstOrDefault(item => item.RuleId == ruleId));
        public Task UpsertAsync(AlarmDefinition definition, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisableAsync(string ruleId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
