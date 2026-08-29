using IndustrialDAQ.Alarm.Center;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndustrialDAQ.Alarm.Notifications;

/// <summary>订阅报警广播并可靠调用已配置的外部通知适配器。</summary>
public sealed class AlarmNotificationDispatcher : IHostedService
{
    private readonly IAlarmCenterEventBus _eventBus;
    private readonly WebhookAlarmNotificationAdapter _webhook;
    private readonly ILogger<AlarmNotificationDispatcher> _logger;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public AlarmNotificationDispatcher(IAlarmCenterEventBus eventBus, WebhookAlarmNotificationAdapter webhook, ILogger<AlarmNotificationDispatcher> logger) =>
        (_eventBus, _webhook, _logger) = (eventBus, webhook, logger);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = _eventBus.Subscribe();
        _task = Task.Run(async () =>
        {
            await foreach (var alarmEvent in reader.ReadAllAsync(_cts.Token))
            {
                if (!_webhook.IsConfigured) continue;
                try { await _webhook.SendAsync(alarmEvent, _cts.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "外部报警通知发送失败: {AlarmId}", alarmEvent.Record.Id); }
            }
        }, _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_task is not null) try { await _task.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
        _cts?.Dispose();
    }
}
