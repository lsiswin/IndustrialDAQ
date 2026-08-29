using IndustrialDAQ.Alarm.Center;

namespace IndustrialDAQ.Alarm.Notifications;

/// <summary>报警外部通知适配器契约。</summary>
public interface IAlarmNotificationAdapter
{
    string Name { get; }
    bool IsConfigured { get; }
    Task SendAsync(AlarmCenterEvent alarmEvent, CancellationToken cancellationToken = default);
}
