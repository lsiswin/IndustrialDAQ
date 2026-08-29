// File: DashboardViewModel.cs  Module: UI (ViewModels)  Author: IndustrialDAQ Team
using System.Collections.ObjectModel;
using System.Threading.Channels;
using System.Windows;
using IndustrialDAQ.Acquisition;
using IndustrialDAQ.Alarm;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Storage;
using IndustrialDAQ.UI.Models;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Prism.Mvvm;
using Prism.Navigation;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Navigation.Regions;
using IndustrialDAQ.UI.Services;
using Prism.Events;
using IndustrialDAQ.UI.Events;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>
/// 实时仪表板 ViewModel — 提供首页仪表盘的数据绑定，
/// 包含顶部卡片数据、生产流程工位状态以及实时报警栏数据。
/// </summary>
public class DashboardViewModel : BindableBase, IDestructible
{
    private readonly RealTimeStore _realTimeStore;
    private readonly AcquisitionHost _acquisitionHost;
    private readonly AlarmManager _alarmManager;
    private readonly IAuthManager _authManager;
    private readonly IDialogService _dialogService;
    private readonly IEventAggregator _eventAggregator;
    private CancellationTokenSource? _cts;
    private ChannelReader<TagValue>? _realtimeReader;
    private readonly Dictionary<string, StationModel> _stationLookup = new(StringComparer.OrdinalIgnoreCase);
    private bool _lineRunning;
    private bool _lineAlarmActive;
    private bool _valveOpen;

    // ─── 顶部属性 ───

    private int _totalYield;
    public int TotalYield
    {
        get => _totalYield;
        set => SetProperty(ref _totalYield, value);
    }

    private double _yieldRate;
    public double YieldRate
    {
        get => _yieldRate;
        set => SetProperty(ref _yieldRate, value);
    }

    private double _energyConsumption;
    public double EnergyConsumption
    {
        get => _energyConsumption;
        set => SetProperty(ref _energyConsumption, value);
    }

    private double _currentLevel;
    /// <summary>灌装液位实时值，由 mb-ir-level 驱动。</summary>
    public double CurrentLevel
    {
        get => _currentLevel;
        set => SetProperty(ref _currentLevel, value);
    }

    private double _conveyorSpeed;
    /// <summary>传送带实时速度，由 mb-ir-speed 驱动。</summary>
    public double ConveyorSpeed
    {
        get => _conveyorSpeed;
        set => SetProperty(ref _conveyorSpeed, value);
    }

    private string _systemStatus = "Running";
    public string SystemStatus
    {
        get => _systemStatus;
        set => SetProperty(ref _systemStatus, value);
    }

    private string _currentUser = "访客（未登录）";
    public string CurrentUser
    {
        get => _currentUser;
        set => SetProperty(ref _currentUser, value);
    }

    /// <summary>点击用户区域执行登录或退出。</summary>
    public DelegateCommand UserSessionCommand { get; }

    /// <summary>打开报警日志页面。</summary>
    public DelegateCommand OpenAlarmLogCommand { get; }

    private string _currentTime = string.Empty;
    public string CurrentTime
    {
        get => _currentTime;
        set => SetProperty(ref _currentTime, value);
    }

    private int _alarmCount;
    public int AlarmCount
    {
        get => _alarmCount;
        set => SetProperty(ref _alarmCount, value);
    }

    // ─── 集合 ───

    /// <summary>生产线工位集合。</summary>
    public ObservableCollection<StationModel> Stations { get; } = new();

    /// <summary>实时报警栏集合。</summary>
    public ObservableCollection<string> RealTimeAlarms { get; } = new();

    /// <summary>
    /// 初始化仪表板 ViewModel。
    /// </summary>
    public DashboardViewModel(RealTimeStore realTimeStore, AcquisitionHost acquisitionHost,
        AlarmManager alarmManager, IAuthManager authManager, IDialogService dialogService,
        IEventAggregator eventAggregator)
    {
        _realTimeStore = realTimeStore ?? throw new ArgumentNullException(nameof(realTimeStore));
        _acquisitionHost = acquisitionHost ?? throw new ArgumentNullException(nameof(acquisitionHost));
        _alarmManager = alarmManager ?? throw new ArgumentNullException(nameof(alarmManager));
        _authManager = authManager ?? throw new ArgumentNullException(nameof(authManager));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _cts = new CancellationTokenSource();

        UserSessionCommand = new DelegateCommand(OnUserSessionExecute);
        OpenAlarmLogCommand = new DelegateCommand(() =>
            _eventAggregator.GetEvent<NavigationRequestEvent>().Publish("AlarmRecord"));

        // 订阅报警事件
        _alarmManager.AlarmTriggered += OnAlarmTriggered;
        _alarmManager.AlarmCleared += OnAlarmCleared;
        _alarmManager.ActiveAlarmsChanged += OnActiveAlarmsChanged;
        _authManager.CurrentUserChanged += OnCurrentUserChanged;

        InitializeLiveStations();
        RefreshCurrentUser();
        ApplyRealtimeSnapshot();
        _ = SubscribeToRealtimeDataAsync(_cts.Token);
        LoadActiveAlarms();
        StartClock(_cts.Token);
    }

    /// <inheritdoc />
    public void Destroy()
    {
        // 取消订阅报警事件
        _alarmManager.AlarmTriggered -= OnAlarmTriggered;
        _alarmManager.AlarmCleared -= OnAlarmCleared;
        _alarmManager.ActiveAlarmsChanged -= OnActiveAlarmsChanged;
        _authManager.CurrentUserChanged -= OnCurrentUserChanged;

        if (_realtimeReader is not null)
        {
            _realTimeStore.Unsubscribe(_realtimeReader);
            _realtimeReader = null;
        }

        _cts?.Cancel();
        _cts?.Dispose();
    }

    private void InitializeLiveStations()
    {
        // 首页只展示 production-line.json 中 Modbus-PLC 的真实生产环节。
        AddStation(new StationModel
        {
            StationId = "line",
            Icon = "🏭",
            Name = "生产线",
            Status = StationStatus.NotStarted,
            PrimaryStatName = "模式",
            PrimaryStatValue = "等待数据",
            PlcCount = 1
        });

        AddStation(new StationModel
        {
            StationId = "conveyor",
            Icon = "➡",
            Name = "传送带",
            Status = StationStatus.NotStarted,
            PrimaryStatName = "速度",
            PrimaryStatValue = "-- m/min",
            PlcCount = 1
        });

        AddStation(new StationModel
        {
            StationId = "filling",
            Icon = "💧",
            Name = "灌装站",
            Status = StationStatus.NotStarted,
            PrimaryStatName = "液位",
            PrimaryStatValue = "-- mL",
            PlcCount = 1
        });

        AddStation(new StationModel
        {
            StationId = "counter",
            Icon = "∑",
            Name = "产量计数",
            Status = StationStatus.NotStarted,
            PrimaryStatName = "产量",
            PrimaryStatValue = "0 (瓶)",
            PlcCount = 1,
            IsLast = true
        });
    }

    private void AddStation(StationModel station)
    {
        Stations.Add(station);
        _stationLookup[station.StationId] = station;
    }

    /// <summary>使用实时缓存初始化首页，避免进入页面后等待下一采集周期。</summary>
    private void ApplyRealtimeSnapshot()
    {
        foreach (var value in _realTimeStore.GetAll())
            UpdateDashboard(value);
    }

    /// <summary>订阅采集广播流，使首页与 Python Modbus 模拟器实时联动。</summary>
    private async Task SubscribeToRealtimeDataAsync(CancellationToken ct)
    {
        _realtimeReader = _realTimeStore.Subscribe();
        try
        {
            await foreach (var value in _realtimeReader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Application.Current?.Dispatcher.Invoke(() => UpdateDashboard(value));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>将配置中的 Modbus Tag 映射到首页指标和工位状态。</summary>
    private void UpdateDashboard(TagValue value)
    {
        if (value.Quality == Quality.Bad)
        {
            SystemStatus = "Disconnected";
            return;
        }

        SystemStatus = _acquisitionHost.GetDevices().Count > 0 ? "Running" : "No Device";

        switch (value.TagId)
        {
            case "mb-ir-count" when TryGetDouble(value.Value, out var count):
                TotalYield = Math.Max(0, (int)Math.Round(count));
                _stationLookup["counter"].PrimaryStatValue = $"{TotalYield:N0} (瓶)";
                _stationLookup["counter"].Status = StationStatus.Running;
                break;
            case "mb-ir-level" when TryGetDouble(value.Value, out var level):
                CurrentLevel = Math.Round(level, 1);
                _stationLookup["filling"].PrimaryStatValue = $"{CurrentLevel:F1} mL";
                break;
            case "mb-ir-speed" when TryGetDouble(value.Value, out var speed):
                ConveyorSpeed = Math.Round(speed, 1);
                _stationLookup["conveyor"].PrimaryStatValue = $"{ConveyorSpeed:F1} m/min";
                break;
            case "mb-di-running" when TryGetBool(value.Value, out var running):
                _lineRunning = running;
                UpdateLineAndFillingStatus();
                break;
            case "mb-di-conveyor" when TryGetBool(value.Value, out var conveyorRunning):
                _stationLookup["conveyor"].Status = conveyorRunning ? StationStatus.Running : StationStatus.Standby;
                break;
            case "mb-di-valve" when TryGetBool(value.Value, out var valveOpen):
                _valveOpen = valveOpen;
                UpdateLineAndFillingStatus();
                break;
            case "mb-di-alarm" when TryGetBool(value.Value, out var alarmActive):
                _lineAlarmActive = alarmActive;
                UpdateLineAndFillingStatus();
                break;
        }
    }

    /// <summary>
    /// 根据产线运行、报警和阀门阶段统一计算灌装站状态。
    /// 液位高低只作为过程值，不直接决定工位运行/待机。
    /// </summary>
    private void UpdateLineAndFillingStatus()
    {
        var lineStation = _stationLookup["line"];
        var fillingStation = _stationLookup["filling"];

        if (_lineAlarmActive)
        {
            lineStation.Status = StationStatus.Fault;
            fillingStation.Status = StationStatus.Fault;
            lineStation.PrimaryStatValue = "设备报警";
            fillingStation.PrimaryStatName = "报警液位";
            return;
        }

        lineStation.Status = _lineRunning ? StationStatus.Running : StationStatus.Standby;
        lineStation.PrimaryStatValue = _lineRunning ? "自动运行" : "已停止";
        fillingStation.Status = _lineRunning ? StationStatus.Running : StationStatus.Standby;
        fillingStation.PrimaryStatName = _lineRunning
            ? (_valveOpen ? "灌装中" : "排空中")
            : "待机液位";
    }

    private void OnCurrentUserChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.Invoke(RefreshCurrentUser);

    private void RefreshCurrentUser()
    {
        var user = _authManager.CurrentUser;
        CurrentUser = user.Id == "guest"
            ? "访客（点击登录）"
            : $"{user.RealName}（点击退出）";
    }

    private void OnUserSessionExecute()
    {
        if (_authManager.CurrentUser.Id != "guest")
        {
            _authManager.Logout();
            return;
        }

        _dialogService.ShowDialog("LoginDialog", result =>
        {
            if (result.Result == ButtonResult.OK)
                RefreshCurrentUser();
        });
    }

    private static bool TryGetDouble(object? value, out double result) =>
        double.TryParse(value?.ToString(), out result);

    private static bool TryGetBool(object? value, out bool result)
    {
        if (value is bool boolValue)
        {
            result = boolValue;
            return true;
        }

        if (bool.TryParse(value?.ToString(), out result))
            return true;

        if (int.TryParse(value?.ToString(), out var number))
        {
            result = number != 0;
            return true;
        }

        result = false;
        return false;
    }

    /// <summary>
    /// 加载当前活跃报警到实时报警栏。
    /// </summary>
    private void LoadActiveAlarms()
    {
        var activeAlarms = _alarmManager.GetActiveAlarms();
        RealTimeAlarms.Clear();
        foreach (var alarm in activeAlarms)
        {
            string statusLabel = alarm.Status == AlarmStatus.Active ? "Unack" : "Ack";
            RealTimeAlarms.Add($"[{alarm.OccurredAt.ToLocalTime():HH:mm:ss}] {alarm.Title} ({statusLabel})");
        }
        AlarmCount = activeAlarms.Count;
    }

    /// <summary>
    /// 报警触发事件 — 添加到实时报警栏。
    /// </summary>
    private void OnAlarmTriggered(object? sender, AlarmEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            string statusLabel = e.Record.Status == AlarmStatus.Active ? "Unack" : "Ack";
            string alarmText = $"[{e.Record.OccurredAt.ToLocalTime():HH:mm:ss}] {e.Record.Title} ({statusLabel})";

            // 避免重复添加相同报警
            if (!RealTimeAlarms.Any(a => a.Contains(e.Record.Title)))
            {
                RealTimeAlarms.Insert(0, alarmText);
            }

            AlarmCount = _alarmManager.GetActiveAlarms().Count;
        });
    }

    /// <summary>
    /// 报警恢复事件 — 从实时报警栏移除。
    /// </summary>
    private void OnAlarmCleared(object? sender, AlarmEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            // 移除匹配的报警条目
            var toRemove = RealTimeAlarms.FirstOrDefault(a => a.Contains(e.Record.Title));
            if (toRemove != null)
            {
                RealTimeAlarms.Remove(toRemove);
            }

            AlarmCount = _alarmManager.GetActiveAlarms().Count;
        });
    }

    /// <summary>
    /// 实时报警列表变更 — 同步刷新计数。
    /// </summary>
    private void OnActiveAlarmsChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            AlarmCount = _alarmManager.GetActiveAlarms().Count;
        });
    }

    private async void StartClock(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                CurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                await Task.Delay(1000, ct);
            }
        }
        catch (TaskCanceledException)
        {
            // 忽略任务取消异常
        }
    }
}
