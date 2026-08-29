using System.Threading.Channels;

namespace IndustrialDAQ.Alarm.Center;

public interface IAlarmCenterEventBus
{
    ChannelReader<AlarmCenterEvent> Reader { get; }

    /// <summary>为独立消费者创建广播订阅，避免多个读取者竞争同一个事件。</summary>
    ChannelReader<AlarmCenterEvent> Subscribe();

    ValueTask PublishAsync(AlarmCenterEvent alarmEvent, CancellationToken cancellationToken = default);
}
