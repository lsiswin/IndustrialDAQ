using Prism.Events;

namespace IndustrialDAQ.UI.Events;

public enum NotificationType
{
    Info,
    Success,
    Warning,
    Error
}

public class NotificationMessage
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationType Type { get; set; } = NotificationType.Info;
    public int DurationMs { get; set; } = 3000;

    /// <summary>通知关联键，用于合并同一报警规则的重复弹窗。</summary>
    public string? CorrelationKey { get; set; }

    /// <summary>点击后导航到的页面（可选）。</summary>
    public string? NavigateTo { get; set; }

    public string Icon => Type switch
    {
        NotificationType.Success => "✅",
        NotificationType.Error => "❌",
        NotificationType.Warning => "⚠️",
        _ => "ℹ️"
    };

    public string Color => Type switch
    {
        NotificationType.Success => "#10B981",
        NotificationType.Error => "#EF4444",
        NotificationType.Warning => "#F59E0B",
        _ => "#3B82F6"
    };
}

public class NotificationEvent : PubSubEvent<NotificationMessage> { }
