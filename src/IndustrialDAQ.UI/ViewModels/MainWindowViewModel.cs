using System.Collections.ObjectModel;
using IndustrialDAQ.Alarm;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.UI.Events;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Navigation.Regions;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>
/// Shell 窗口 ViewModel — 管理侧边栏导航和全局状态栏。
/// </summary>
public class MainWindowViewModel : BindableBase
{
    private readonly IRegionManager _regionManager;
    private readonly IEventAggregator _eventAggregator;
    private readonly AlarmManager _alarmManager;

    /// <summary>全局通知集合。</summary>
    public ObservableCollection<NotificationMessage> Notifications { get; } = new();

    private string _currentPage = "首页";
    /// <summary>当前页面标题。</summary>
    public string CurrentPage { get => _currentPage; set => SetProperty(ref _currentPage, value); }

    private string _statusMessage = "就绪";
    /// <summary>状态栏消息。</summary>
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    private bool _isConnected;
    /// <summary>采集引擎连接状态。</summary>
    public bool IsConnected { get => _isConnected; set => SetProperty(ref _isConnected, value); }

    private int _activeAlarmCount;
    /// <summary>活跃报警数量。</summary>
    public int ActiveAlarmCount
    {
        get => _activeAlarmCount;
        set
        {
            if (SetProperty(ref _activeAlarmCount, value))
            {
                AlarmBadgeVisibility = value > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }
        }
    }

    private System.Windows.Visibility _alarmBadgeVisibility = System.Windows.Visibility.Collapsed;
    /// <summary>报警角标可见性。</summary>
    public System.Windows.Visibility AlarmBadgeVisibility
    {
        get => _alarmBadgeVisibility;
        set => SetProperty(ref _alarmBadgeVisibility, value);
    }

    /// <summary>统一导航命令，参数为视图名称。</summary>
    public DelegateCommand<string> NavigateCommand { get; }

    /// <summary>手动关闭通知命令。</summary>
    public DelegateCommand<NotificationMessage> CloseNotificationCommand { get; }

    /// <summary>点击通知命令。</summary>
    public DelegateCommand<NotificationMessage> NotificationClickCommand { get; }

    public MainWindowViewModel(IRegionManager regionManager, IEventAggregator eventAggregator,
        AlarmManager alarmManager)
    {
        _regionManager = regionManager;
        _eventAggregator = eventAggregator;
        _alarmManager = alarmManager;

        NavigateCommand = new DelegateCommand<string>(page =>
        {
            if (string.IsNullOrEmpty(page)) return;

            string viewName = page switch
            {
                "Dashboard" => "DashboardView",
                "ProductionMonitor" => "ProductionMonitorView",
                "DeviceDetail" => "DeviceDetailView",
                "AlarmRecord" => "AlarmRecordView",
                "Trend" => "TrendView",
                "VisionInspection" => "VisionInspectionView",
                "DeviceTemplate" => "DeviceTemplateView",
                "SystemSettings" => "SystemSettingsView",
                _ => page
            };

            CurrentPage = page switch
            {
                "Dashboard" => "生产监控中心",
                "ProductionMonitor" => "设备详情",
                "DeviceDetail" => "数据点详情",
                "AlarmRecord" => "警报日志",
                "Trend" => "趋势监控",
                "VisionInspection" => "机器视觉检测",
                "DeviceTemplate" => "设备模板",
                "SystemSettings" => "系统设置",
                _ => page
            };

            _regionManager.RequestNavigate("MainRegion", viewName);
            StatusMessage = $"📍 {CurrentPage} — 已就绪";
        });

        CloseNotificationCommand = new DelegateCommand<NotificationMessage>(msg =>
        {
            if (msg != null && Notifications.Contains(msg))
                Notifications.Remove(msg);
        });

        NotificationClickCommand = new DelegateCommand<NotificationMessage>(msg =>
        {
            if (msg == null) return;

            // 移除通知
            if (Notifications.Contains(msg))
                Notifications.Remove(msg);

            // 如果有导航目标，跳转到对应页面
            if (!string.IsNullOrEmpty(msg.NavigateTo))
            {
                NavigateCommand.Execute(msg.NavigateTo);
            }
        });

        // 订阅全局通知事件
        _eventAggregator.GetEvent<NotificationEvent>().Subscribe(OnNotificationReceived);

        // 页面内部导航统一回到 Shell 命令，避免内容已切换但菜单和标题仍停留在旧页面。
        _eventAggregator.GetEvent<NavigationRequestEvent>().Subscribe(page => NavigateCommand.Execute(page));

        // 订阅报警事件
        _alarmManager.AlarmTriggered += OnAlarmTriggered;
        _alarmManager.AlarmCleared += OnAlarmCleared;

        IsConnected = true;
        StatusMessage = "📍 首页 — 已就绪";

        // 加载当前活跃报警数
        ActiveAlarmCount = _alarmManager.GetActiveAlarms().Count;
    }

    /// <summary>
    /// 处理报警触发事件 — 显示全局通知。
    /// </summary>
    private void OnAlarmTriggered(object? sender, AlarmEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ActiveAlarmCount = _alarmManager.GetActiveAlarms().Count;

            // 创建通知消息
            var notificationType = e.Record.Severity switch
            {
                AlarmSeverity.Critical => NotificationType.Error,
                AlarmSeverity.Warning => NotificationType.Warning,
                _ => NotificationType.Info
            };

            var notification = new NotificationMessage
            {
                Title = e.Record.Title,
                Message = $"{e.Record.TagName}: {e.Record.Message}",
                Type = notificationType,
                DurationMs = 0,  // 报警通知不自动消失，需要手动关闭或点击
                NavigateTo = "AlarmRecord",  // 点击跳转到报警日志
                CorrelationKey = e.Record.RuleId
            };

            // 同一规则在活跃期间只保留一个弹窗，避免重复事件阻塞界面操作。
            var existingNotification = Notifications.FirstOrDefault(item =>
                item.CorrelationKey == notification.CorrelationKey);
            if (existingNotification is not null)
            {
                Notifications.Remove(existingNotification);
            }

            Notifications.Add(notification);

            // 自动消失
            if (notification.DurationMs > 0)
            {
                _ = Task.Delay(notification.DurationMs).ContinueWith(_ =>
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (Notifications.Contains(notification))
                            Notifications.Remove(notification);
                    });
                });
            }
        });
    }

    /// <summary>
    /// 处理报警清除事件。
    /// </summary>
    private void OnAlarmCleared(object? sender, AlarmEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ActiveAlarmCount = _alarmManager.GetActiveAlarms().Count;

            // 报警恢复后关闭对应规则的弹窗；完整触发记录仍保留在报警日志中。
            var notificationsToRemove = Notifications
                .Where(item => item.CorrelationKey == e.Record.RuleId)
                .ToList();
            foreach (var notification in notificationsToRemove)
            {
                Notifications.Remove(notification);
            }
        });
    }

    private void OnNotificationReceived(NotificationMessage message)
    {
        if (message == null) return;

        // 在 UI 线程添加通知
        System.Windows.Application.Current.Dispatcher.Invoke(async () =>
        {
            Notifications.Add(message);

            // 自动消失逻辑
            if (message.DurationMs > 0)
            {
                await Task.Delay(message.DurationMs);
                if (Notifications.Contains(message))
                {
                    Notifications.Remove(message);
                }
            }
        });
    }
}
