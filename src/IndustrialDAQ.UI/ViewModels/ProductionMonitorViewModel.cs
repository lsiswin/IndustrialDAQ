// File: ProductionMonitorViewModel.cs  Module: UI (ViewModels)  Author: IndustrialDAQ Team
using System.Collections.ObjectModel;
using System.Windows;
using IndustrialDAQ.Acquisition;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Storage;
using IndustrialDAQ.UI.Models;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using IndustrialDAQ.UI.Events;
using IndustrialDAQ.UI.Services;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>
/// 灌装产线生产监控系统 ViewModel — 设备选择、仪表盘、实时数据表。
/// 趋势图功能已迁移至专用的趋势监控页面（TrendView）。
/// </summary>
public class ProductionMonitorViewModel : BindableBase, IDestructible
{
    private readonly RealTimeStore _realTimeStore;
    private readonly AcquisitionHost _acquisitionHost;
    private readonly IDialogService _dialogService;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, TagDisplayItem> _itemLookup = new();
    private readonly IAuthManager _authManager;
    public bool CanModify => _authManager.CanModify;

    // ─── 设备选择 ───

    /// <summary>可选设备列表。</summary>
    public ObservableCollection<DeviceConfig> Devices { get; } = new();

    private DeviceConfig? _selectedDevice;
    /// <summary>当前选中的设备。</summary>
    public DeviceConfig? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value) && value is not null)
            {
                OnDeviceSelected(value);
                // 切换设备时立即刷新一次连接状态和报警信息
                var driver = _acquisitionHost.GetDriver(value.Id);
                IsDeviceConnected = driver?.IsConnected ?? false;
                UpdateAlarmStatus(null);
            }
        }
    }

    // ─── 报警 ───

    private bool _hasAlarm;
    /// <summary>是否有活跃报警。</summary>
    public bool HasAlarm { get => _hasAlarm; set => SetProperty(ref _hasAlarm, value); }

    private string _alarmText = "系统正常，无活跃报警";
    /// <summary>报警栏文本。</summary>
    public string AlarmText { get => _alarmText; set => SetProperty(ref _alarmText, value); }

    private bool _isDeviceConnected = true;
    private readonly IEventAggregator _eventAggregator;

    /// <summary>当前选中设备是否已连接。</summary>
    public bool IsDeviceConnected { get => _isDeviceConnected; set => SetProperty(ref _isDeviceConnected, value); }

    // ─── 仪表盘 ───

    public ObservableCollection<GaugeItem> Gauges { get; } = new();

    // ─── 实时数据表 ───

    public ObservableCollection<TagDisplayItem> TagTable { get; } = new();

    // ─── 导航与命令 ───

    public DelegateCommand NavigateBackCommand { get; }
    public DelegateCommand<TagDisplayItem> WriteTagCommand { get; }

    public ProductionMonitorViewModel(
        RealTimeStore realTimeStore,
        AcquisitionHost acquisitionHost,
        IEventAggregator eventAggregator,
        IDialogService dialogService,
        IAuthManager authManager)
    {
        _realTimeStore = realTimeStore ?? throw new ArgumentNullException(nameof(realTimeStore));
        _acquisitionHost = acquisitionHost ?? throw new ArgumentNullException(nameof(acquisitionHost));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _authManager = authManager ?? throw new ArgumentNullException(nameof(authManager));

        // 订阅配置重载事件
        eventAggregator.GetEvent<ConfigurationReloadedEvent>().Subscribe(LoadDevices);

        NavigateBackCommand = new DelegateCommand(() => { });
        WriteTagCommand = new DelegateCommand<TagDisplayItem>(OnWriteTag, _ => CanModify);
        _authManager.CurrentUserChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(CanModify));
            WriteTagCommand.RaiseCanExecuteChanged();
        };

        // 延迟加载设备列表（等待 AcquisitionHost 初始化完成）
        _cts = new CancellationTokenSource();
        _ = SubscribeToRealtimeDataAsync(_cts.Token);
        _ = StartConnectionCheckLoop(_cts.Token);

        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(500);
            LoadDevices();
        });
    }

    private void LoadDevices()
    {
        var deviceList = _acquisitionHost.GetDevices();
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Devices.Clear();
            foreach (var d in deviceList)
                Devices.Add(d);
            if (Devices.Count > 0)
                SelectedDevice = Devices[0];
        });
    }

    private void OnDeviceSelected(DeviceConfig device)
    {
        TagTable.Clear();
        _itemLookup.Clear();
        Gauges.Clear();

        var driver = _acquisitionHost.GetDriver(device.Id);
        bool isConnected = driver?.IsConnected ?? false;

        foreach (var tag in device.Tags)
        {
            var item = new TagDisplayItem(tag.Id)
            {
                TagName = tag.Name,
                Value = "-",
                Quality = isConnected ? "Good" : "Bad",
                Timestamp = "-",
                Description = tag.Description,
                IsNumeric = IsNumericType(tag.DataType),
                CanWrite = tag.Access == TagAccess.Write || tag.Access == TagAccess.ReadWrite,
            };
            _itemLookup[tag.Id] = item;
            TagTable.Add(item);

            // 为所有模拟量 Tag 自动添加仪表
            if (IsNumericType(tag.DataType))
            {
                Gauges.Add(new GaugeItem(tag.Id)
                {
                    Label = string.IsNullOrWhiteSpace(tag.Description) ? tag.Name : tag.Description,
                    Value = 0,
                    GaugeColor = "#3B82F6",
                    MaxValue = 100
                });
            }
        }
    }

    private void OnWriteTag(TagDisplayItem? item)
    {
        // 命令层再次校验，避免仅依赖按钮隐藏造成越权写入。
        if (!CanModify || item == null || _selectedDevice == null) return;

        var targetTag = _selectedDevice.Tags.FirstOrDefault(t => t.Id == item.TagId);
        if (targetTag == null) return;

        var parameters = new DialogParameters
        {
            { "TagName", string.IsNullOrWhiteSpace(item.Description) ? item.TagName : item.Description },
            { "DataType", targetTag.DataType },
            { "CurrentValue", item.Value }
        };

        _dialogService.ShowDialog("WriteTagDialog", parameters, result =>
        {
            if (result.Result != ButtonResult.OK) return;

            string stringValue = result.Parameters.GetValue<string>("ResultValue");
            if (string.IsNullOrWhiteSpace(stringValue)) return;

            object? writeValue = null;
            try
            {
                writeValue = targetTag.DataType switch
                {
                    TagDataType.Bool => bool.Parse(stringValue),
                    TagDataType.Int16 => short.Parse(stringValue),
                    TagDataType.Int32 => int.Parse(stringValue),
                    TagDataType.Float32 => float.Parse(stringValue),
                    TagDataType.Float64 => double.Parse(stringValue),
                    TagDataType.UInt16 => ushort.Parse(stringValue),
                    TagDataType.UInt32 => uint.Parse(stringValue),
                    TagDataType.String => stringValue,
                    _ => stringValue
                };
            }
            catch
            {
                _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                {
                    Title = "写入失败",
                    Message = $"写入格式不正确，无法转换为 {targetTag.DataType}",
                    Type = NotificationType.Error
                });
                return;
            }

            var driver = _acquisitionHost.GetDriver(_selectedDevice.Id);
            if (driver != null)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await driver.WriteTagAsync(targetTag, writeValue, CancellationToken.None);
                        _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                        {
                            Title = "写入成功",
                            Message = $"测点 [{targetTag.Name}] 写入指令已下发",
                            Type = NotificationType.Success
                        });
                    }
                    catch (Exception ex)
                    {
                        _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                        {
                            Title = "写入错误",
                            Message = ex.Message,
                            Type = NotificationType.Error
                        });
                    }
                });
            }
            else
            {
                _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                {
                    Title = "写入失败",
                    Message = "无法获取设备驱动实例，请检查设备是否连接",
                    Type = NotificationType.Error
                });
            }
        });
    }

    /// <inheritdoc />
    public void Destroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    /// <summary>
    /// 后台消费实时数据，更新仪表和数据表。
    /// </summary>
    private async Task SubscribeToRealtimeDataAsync(CancellationToken ct)
    {
        try
        {
            var reader = _realTimeStore.Subscribe();
            await foreach (TagValue value in reader.ReadAllAsync(ct)
                .ConfigureAwait(false))
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    UpdateGaugeFromTag(value);
                    UpdateDataTable(value);
                    UpdateAlarmStatus(value);
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void UpdateGaugeFromTag(TagValue value)
    {
        if (value.Value is null || value.Quality == Quality.Bad) return;

        var gauge = Gauges.FirstOrDefault(g => g.TagId == value.TagId);
        if (gauge != null && double.TryParse(value.Value.ToString(), out double val))
        {
            gauge.Value = Math.Round(val, 2);
        }
    }

    private void UpdateDataTable(TagValue value)
    {
        if (_itemLookup.TryGetValue(value.TagId, out TagDisplayItem? item))
        {
            item.TagName = value.TagName;
            item.Value = value.Value?.ToString() ?? "-";
            item.Quality = value.Quality.ToString();
            item.Timestamp = value.Timestamp.LocalDateTime.ToString("HH:mm:ss");
        }
    }

    private void UpdateAlarmStatus(TagValue? value)
    {
        if (_selectedDevice == null) return;

        // 1. 优先检查物理连接状态
        if (!IsDeviceConnected)
        {
            HasAlarm = true;
            AlarmText = $" [{_selectedDevice.Name}] 连接失败，请检查后端服务、网络或设备配置！";
            return;
        }

        // 2. 检查报警相关标签
        if (value != null)
        {
            if (value.TagName == "Line.AlarmActive" && value.Value is bool alarmActive && alarmActive)
            {
                HasAlarm = true;
                AlarmText = $" [{_selectedDevice.Name}] 设备报警：产线异常，请检查！";
                return;
            }
            else if (value.TagName == "Line.AlarmActive")
            {
                HasAlarm = false;
                AlarmText = "系统正常，无活跃报警";
                return;
            }
        }

        // 3. 如果连接正常且没有新的报警标签触发，维持当前状态或根据连接恢复刷新
        if (HasAlarm && AlarmText.Contains("连接失败"))
        {
            HasAlarm = false;
            AlarmText = "系统正常，无活跃报警";
        }
        else if (!HasAlarm && AlarmText.Contains("连接失败"))
        {
            // 防止状态不一致
            AlarmText = "系统正常，无活跃报警";
        }
    }

    /// <summary>
    /// 定期检查设备连接状态的任务。
    /// </summary>
    private async Task StartConnectionCheckLoop(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (SelectedDevice != null)
                {
                    var driver = _acquisitionHost.GetDriver(SelectedDevice.Id);
                    bool connected = driver?.IsConnected ?? false;

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (IsDeviceConnected != connected)
                        {
                            IsDeviceConnected = connected;
                            UpdateAlarmStatus(null);

                            // 无论连上还是断开，都同步更新表格状态
                            foreach (var item in TagTable)
                            {
                                if (connected)
                                {
                                    // 恢复连接时，将 Bad 状态改为 Good (等待下一次真实数据刷新)
                                    if (item.Quality == "Bad") item.Quality = "Good";
                                }
                                else
                                {
                                    // 断开连接时，标记为 Bad
                                    if (item.Quality != "Init") item.Quality = "Bad";
                                }
                            }
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool IsNumericType(TagDataType dt) => dt switch
    {
        TagDataType.Float32 or TagDataType.Float64 or TagDataType.Int16
            or TagDataType.Int32 or TagDataType.Int64 or TagDataType.UInt16
            or TagDataType.UInt32 => true,
        _ => false
    };
}
