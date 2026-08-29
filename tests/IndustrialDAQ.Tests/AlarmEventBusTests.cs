using IndustrialDAQ.Alarm.Center;
using IndustrialDAQ.Alarm.StateMachine;
using IndustrialDAQ.Core.Models;

namespace IndustrialDAQ.Tests;

public sealed class AlarmEventBusTests
{
    [Fact]
    public async Task Publish_BroadcastsToEverySubscriber()
    {
        var bus = new AlarmCenterEventBus();
        var first = bus.Subscribe();
        var second = bus.Subscribe();
        var alarmEvent = new AlarmCenterEvent
        {
            EventType = AlarmCenterEventType.Raised,
            Record = new AlarmRecord { Id = "alarm-1", RuleId = "rule-1" },
            Transition = new AlarmStateTransition { OccurrenceId = "occ-1", RuleId = "rule-1", Definition = new AlarmDefinition { RuleId = "rule-1" } }
        };

        await bus.PublishAsync(alarmEvent);

        Assert.Same(alarmEvent, await first.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Same(alarmEvent, await second.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
    }
}
