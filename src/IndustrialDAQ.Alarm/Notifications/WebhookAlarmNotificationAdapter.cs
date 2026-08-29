using System.Net.Http.Json;
using IndustrialDAQ.Alarm.Center;
using Microsoft.Extensions.Logging;

namespace IndustrialDAQ.Alarm.Notifications;

/// <summary>钉钉、企业微信等文本机器人 Webhook 通知适配器。</summary>
public sealed class WebhookAlarmNotificationAdapter : IAlarmNotificationAdapter
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ILogger<WebhookAlarmNotificationAdapter> _logger;
    private readonly string[] _webhookUrls;

    public WebhookAlarmNotificationAdapter(ILogger<WebhookAlarmNotificationAdapter> logger)
    {
        _logger = logger;
        _webhookUrls = (Environment.GetEnvironmentVariable("INDUSTRIALDAQ_ALARM_WEBHOOKS") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public string Name => "Webhook";
    public bool IsConfigured => _webhookUrls.Length > 0;

    public async Task SendAsync(AlarmCenterEvent alarmEvent, CancellationToken cancellationToken = default)
    {
        var record = alarmEvent.Record;
        var content = $"[IndustrialDAQ] {alarmEvent.EventType} | {record.Severity} | {record.Title}\n资源: {record.TargetResourcePath}\n{record.Message}\n时间: {record.OccurredAt:O}";
        foreach (var url in _webhookUrls)
        {
            using var response = await _client.PostAsJsonAsync(url, new { msgtype = "text", text = new { content } }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("报警 Webhook 返回失败: {StatusCode} {Body}", response.StatusCode, body);
                response.EnsureSuccessStatusCode();
            }
        }
    }
}
