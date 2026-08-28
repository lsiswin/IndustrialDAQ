// File: DeviceTemplateViewModel.cs  Module: UI (ViewModels)  Author: IndustrialDAQ Team
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using IndustrialDAQ.Acquisition;
using IndustrialDAQ.Alarm;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Storage;
using IndustrialDAQ.UI.Events;
using IndustrialDAQ.UI.Services;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>
/// 设备模板页面 ViewModel — 浏览预定义设备模板、数据点及其报警/趋势配置。
/// 支持从模板生成设备配置 JSON、从已连接设备创建新模板。
/// 模板数据持久化到 SQLite 数据库。
/// </summary>
public class DeviceTemplateViewModel : BindableBase
{
    private readonly AlarmManager _alarmManager;
    private readonly IEventAggregator _eventAggregator;
    private readonly IDialogService _dialogService;
    private readonly DeviceTemplateRepository _templateRepository;
    private readonly AcquisitionHost _acquisitionHost;
    private readonly IAuthManager _authManager;
    public bool CanModify => _authManager.CanModify;

    // ─── 左侧模板列表 ───

    public ObservableCollection<DeviceTemplateItem> DeviceTemplates { get; } = [];

    private DeviceTemplateItem? _selectedTemplate;
    public DeviceTemplateItem? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value) && value is not null)
                OnTemplateSelected(value.Template);
        }
    }

    // ─── 右侧模板信息 ───

    private string _templateId = string.Empty;
    public string TemplateId { get => _templateId; set => SetProperty(ref _templateId, value); }

    private string _templateName = string.Empty;
    public string TemplateName { get => _templateName; set => SetProperty(ref _templateName, value); }

    private string _driverType = string.Empty;
    public string DriverType { get => _driverType; set => SetProperty(ref _driverType, value); }

    private string _driverIcon = string.Empty;
    public string DriverIcon { get => _driverIcon; set => SetProperty(ref _driverIcon, value); }

    private string _driverDescription = string.Empty;
    public string DriverDescription { get => _driverDescription; set => SetProperty(ref _driverDescription, value); }

    private int _dataPointCount;
    public int DataPointCount { get => _dataPointCount; set => SetProperty(ref _dataPointCount, value); }

    private int _alarmTemplateCount;
    public int AlarmTemplateCount { get => _alarmTemplateCount; set => SetProperty(ref _alarmTemplateCount, value); }

    private int _trendTemplateCount;
    public int TrendTemplateCount { get => _trendTemplateCount; set => SetProperty(ref _trendTemplateCount, value); }

    // ─── 数据点列表 ───

    public ObservableCollection<DataPointDisplayItem> DataPoints { get; } = [];

    private DataPointDisplayItem? _selectedDataPoint;
    public DataPointDisplayItem? SelectedDataPoint
    {
        get => _selectedDataPoint;
        set
        {
            if (SetProperty(ref _selectedDataPoint, value) && value is not null)
                OnDataPointSelected(value.Template);
        }
    }

    // ─── 报警模板详情 ───

    private bool _hasAlarmTemplate;
    public bool HasAlarmTemplate { get => _hasAlarmTemplate; set { if (SetProperty(ref _hasAlarmTemplate, value)) RaisePropertyChanged(nameof(NoAlarmTemplate)); } }
    public bool NoAlarmTemplate => !HasAlarmTemplate;

    private string _alarmTemplateName = "无";
    public string AlarmTemplateName { get => _alarmTemplateName; set => SetProperty(ref _alarmTemplateName, value); }

    private string _alarmTemplateId = string.Empty;
    public string AlarmTemplateId { get => _alarmTemplateId; set => SetProperty(ref _alarmTemplateId, value); }

    private string _alarmTemplateUnit = string.Empty;
    public string AlarmTemplateUnit { get => _alarmTemplateUnit; set => SetProperty(ref _alarmTemplateUnit, value); }

    public ObservableCollection<AlarmThresholdItem> AlarmThresholds { get; } = [];

    private string _alarmSeverity = string.Empty;
    public string AlarmSeverity { get => _alarmSeverity; set => SetProperty(ref _alarmSeverity, value); }

    private string _alarmCooldown = string.Empty;
    public string AlarmCooldown { get => _alarmCooldown; set => SetProperty(ref _alarmCooldown, value); }

    private string _alarmTypes = string.Empty;
    public string AlarmTypes { get => _alarmTypes; set => SetProperty(ref _alarmTypes, value); }

    // ─── 趋势模板详情 ───

    private bool _hasTrendTemplate;
    public bool HasTrendTemplate { get => _hasTrendTemplate; set { if (SetProperty(ref _hasTrendTemplate, value)) RaisePropertyChanged(nameof(NoTrendTemplate)); } }
    public bool NoTrendTemplate => !HasTrendTemplate;

    private string _trendTemplateName = "无";
    public string TrendTemplateName { get => _trendTemplateName; set => SetProperty(ref _trendTemplateName, value); }

    private string _trendTemplateId = string.Empty;
    public string TrendTemplateId { get => _trendTemplateId; set => SetProperty(ref _trendTemplateId, value); }

    private string _trendUnit = string.Empty;
    public string TrendUnit { get => _trendUnit; set => SetProperty(ref _trendUnit, value); }

    private string _trendYRange = string.Empty;
    public string TrendYRange { get => _trendYRange; set => SetProperty(ref _trendYRange, value); }

    private string _trendWindow = string.Empty;
    public string TrendWindow { get => _trendWindow; set => SetProperty(ref _trendWindow, value); }

    private string _trendBufferSize = string.Empty;
    public string TrendBufferSize { get => _trendBufferSize; set => SetProperty(ref _trendBufferSize, value); }

    private string _trendLineColor = "#3B82F6";
    public string TrendLineColor { get => _trendLineColor; set => SetProperty(ref _trendLineColor, value); }

    private bool _trendShowAlarmLines;
    public bool TrendShowAlarmLines { get => _trendShowAlarmLines; set => SetProperty(ref _trendShowAlarmLines, value); }

    private string _trendStrokeThickness = "2";
    public string TrendStrokeThickness { get => _trendStrokeThickness; set => SetProperty(ref _trendStrokeThickness, value); }

    // ─── 命令 ───

    public DelegateCommand<DeviceTemplateItem> SelectTemplateCommand { get; }
    public DelegateCommand<DataPointDisplayItem> SelectDataPointCommand { get; }
    public DelegateCommand CreateDeviceCommand { get; }
    public DelegateCommand AddTemplateCommand { get; }
    public DelegateCommand RefreshCommand { get; }

    // ─── 状态 ───

    private string _statusText = "选择一个设备模板查看详情";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public DeviceTemplateViewModel(AlarmManager alarmManager, IEventAggregator eventAggregator,
        IDialogService dialogService, DeviceTemplateRepository templateRepository,
        AcquisitionHost acquisitionHost, IAuthManager authManager)
    {
        _alarmManager = alarmManager;
        _eventAggregator = eventAggregator;
        _dialogService = dialogService;
        _templateRepository = templateRepository;
        _acquisitionHost = acquisitionHost;
        _authManager = authManager;

        SelectTemplateCommand = new DelegateCommand<DeviceTemplateItem>(item =>
        {
            if (item is not null) SelectedTemplate = item;
        });
        SelectDataPointCommand = new DelegateCommand<DataPointDisplayItem>(item =>
        {
            if (item is not null) SelectedDataPoint = item;
        });
        CreateDeviceCommand = new DelegateCommand(OnCreateDevice, () => CanModify && SelectedTemplate is not null)
            .ObservesProperty(() => SelectedTemplate);
        AddTemplateCommand = new DelegateCommand(OnAddTemplate, () => CanModify);
        RefreshCommand = new DelegateCommand(async () => await LoadTemplatesAsync());
        _authManager.CurrentUserChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(CanModify));
            CreateDeviceCommand.RaiseCanExecuteChanged();
            AddTemplateCommand.RaiseCanExecuteChanged();
        };

        _ = LoadTemplatesAsync();
    }

    /// <summary>
    /// 从数据库加载所有模板（内置 + 用户创建）。
    /// </summary>
    private async Task LoadTemplatesAsync()
    {
        try
        {
            StatusText = "正在加载模板...";
            var templates = await _templateRepository.LoadAllAsync();

            DeviceTemplates.Clear();
            foreach (var template in templates)
                DeviceTemplates.Add(new DeviceTemplateItem(template));

            if (DeviceTemplates.Count > 0)
                SelectedTemplate = DeviceTemplates[0];

            StatusText = $"已加载 {templates.Count} 个设备模板";
        }
        catch (Exception ex)
        {
            StatusText = $"加载模板失败: {ex.Message}";
        }
    }

    private void OnTemplateSelected(DeviceTemplate template)
    {
        TemplateId = template.TemplateId;
        TemplateName = template.Name;
        DriverType = template.DriverType;
        DriverIcon = template.DriverType switch
        {
            "OpcUA" => "🔗",
            "Modbus" => "📡",
            "S7" => "🏭",
            _ => "⚙️"
        };
        DriverDescription = template.DriverType switch
        {
            "OpcUA" => "OPC UA 协议 — 跨平台工业通信标准",
            "Modbus" => "Modbus TCP 协议 — 串行/以太网通信",
            "S7" => "Siemens S7 协议 — 西门子 PLC 直连",
            _ => "通用协议驱动"
        };
        DataPointCount = template.DataPoints.Count;
        AlarmTemplateCount = template.DataPoints.Count(dp => dp.AlarmTemplate is not null);
        TrendTemplateCount = template.DataPoints.Count(dp => dp.TrendTemplate is not null);

        DataPoints.Clear();
        AlarmThresholds.Clear();
        HasAlarmTemplate = false;
        HasTrendTemplate = false;

        foreach (var dp in template.DataPoints)
            DataPoints.Add(new DataPointDisplayItem(dp));

        if (DataPoints.Count > 0)
            SelectedDataPoint = DataPoints[0];

        StatusText = $"已加载模板: {template.Name} ({template.DataPoints.Count} 个数据点)";
    }

    private void OnDataPointSelected(DataPointTemplate dp)
    {
        // 报警模板详情
        if (dp.AlarmTemplate is not null)
        {
            HasAlarmTemplate = true;
            AlarmTemplateId = dp.AlarmTemplate.TemplateId;
            AlarmTemplateName = dp.AlarmTemplate.Name;
            AlarmTemplateUnit = dp.AlarmTemplate.Unit;
            AlarmSeverity = dp.AlarmTemplate.Severity.ToString();
            AlarmCooldown = $"{dp.AlarmTemplate.CooldownSeconds} 秒";
            AlarmTypes = string.Join(", ", dp.AlarmTemplate.SupportedAlarmTypes.Select(t => t.ToString()));

            AlarmThresholds.Clear();
            if (dp.AlarmTemplate.HighHighThreshold > 0)
                AlarmThresholds.Add(new AlarmThresholdItem("高高限 (HH)", dp.AlarmTemplate.HighHighThreshold, "#EF4444"));
            if (dp.AlarmTemplate.HighThreshold > 0)
                AlarmThresholds.Add(new AlarmThresholdItem("高限 (H)", dp.AlarmTemplate.HighThreshold, "#F59E0B"));
            if (dp.AlarmTemplate.LowThreshold > 0)
                AlarmThresholds.Add(new AlarmThresholdItem("低限 (L)", dp.AlarmTemplate.LowThreshold, "#3B82F6"));
            if (dp.AlarmTemplate.LowLowThreshold > 0)
                AlarmThresholds.Add(new AlarmThresholdItem("低低限 (LL)", dp.AlarmTemplate.LowLowThreshold, "#8B5CF6"));
            if (dp.AlarmTemplate.Hysteresis > 0)
                AlarmThresholds.Add(new AlarmThresholdItem("滞回值 (死区)", dp.AlarmTemplate.Hysteresis, "#9CA3AF"));
        }
        else
        {
            HasAlarmTemplate = false;
            AlarmTemplateId = string.Empty;
            AlarmTemplateName = "未配置";
            AlarmThresholds.Clear();
        }

        // 趋势模板详情
        if (dp.TrendTemplate is not null)
        {
            HasTrendTemplate = true;
            TrendTemplateId = dp.TrendTemplate.TemplateId;
            TrendTemplateName = dp.TrendTemplate.Name;
            TrendUnit = dp.TrendTemplate.Unit;
            TrendLineColor = dp.TrendTemplate.LineColor;
            TrendShowAlarmLines = dp.TrendTemplate.ShowAlarmLines;
            TrendStrokeThickness = dp.TrendTemplate.StrokeThickness.ToString("G");

            string yMin = double.IsNaN(dp.TrendTemplate.YMin) ? "自动" : dp.TrendTemplate.YMin.ToString("G");
            string yMax = double.IsNaN(dp.TrendTemplate.YMax) ? "自动" : dp.TrendTemplate.YMax.ToString("G");
            TrendYRange = $"{yMin} ~ {yMax} {dp.TrendTemplate.Unit}";

            int ws = dp.TrendTemplate.WindowSeconds;
            TrendWindow = ws >= 60 ? $"{ws / 60} 分钟 ({ws} 秒)" : $"{ws} 秒";
            TrendBufferSize = $"{dp.TrendTemplate.BufferCapacity:N0} 点";
        }
        else
        {
            HasTrendTemplate = false;
            TrendTemplateId = string.Empty;
            TrendTemplateName = "未配置";
            TrendYRange = string.Empty;
            TrendWindow = string.Empty;
            TrendBufferSize = string.Empty;
        }
    }

    /// <summary>
    /// 弹出对话框，用户输入设备信息后生成 JSON 配置文件。
    /// </summary>
    private void OnCreateDevice()
    {
        if (SelectedTemplate is null) return;
        var template = SelectedTemplate.Template;

        var parameters = new DialogParameters
        {
            { "TemplateName", template.Name },
            { "DriverType", template.DriverType }
        };

        _dialogService.ShowDialog("CreateDeviceDialog", parameters, result =>
        {
            if (result.Result != ButtonResult.OK) return;

            string deviceName = result.Parameters.GetValue<string>("DeviceName");
            string ipAddress = result.Parameters.GetValue<string>("IpAddress");
            int port = result.Parameters.GetValue<int>("Port");
            int cycleTimeMs = result.Parameters.GetValue<int>("CycleTimeMs");
            int timeoutMs = result.Parameters.GetValue<int>("TimeoutMs");
            int retryCount = result.Parameters.GetValue<int>("RetryCount");

            try
            {
                var config = new DeviceConfig
                {
                    Id = $"device-{template.TemplateId}-{DateTime.Now:yyyyMMddHHmmss}",
                    Name = deviceName,
                    DriverType = template.DriverType,
                    IpAddress = ipAddress,
                    Port = port,
                    CycleTimeMs = cycleTimeMs,
                    TimeoutMs = timeoutMs,
                    RetryCount = retryCount,
                    Tags = template.DataPoints.Select((dp, i) => new TagPoint
                    {
                        Id = $"tag-{dp.TemplateId}",
                        Name = dp.Name,
                        Address = $"4000{i + 1}",
                        DataType = dp.DataType,
                        Access = TagAccess.Read,
                        Description = $"{dp.Name} ({dp.Unit})",
                        TemplateId = dp.TemplateId
                    }).ToList()
                };

                string json = JsonSerializer.Serialize(new[] { config }, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                string configDir = FindConfigDirectory("config");
                Directory.CreateDirectory(configDir);
                string filePath = Path.Combine(configDir, $"{template.TemplateId}.json");
                File.WriteAllText(filePath, json);

                StatusText = $"已生成设备配置: {filePath}";
                _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                {
                    Title = "设备配置已生成",
                    Message = $"模板 [{template.Name}] 的配置已保存到 {filePath}",
                    Type = NotificationType.Success
                });
            }
            catch (Exception ex)
            {
                StatusText = $"生成配置失败: {ex.Message}";
                _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                {
                    Title = "生成失败",
                    Message = ex.Message,
                    Type = NotificationType.Error
                });
            }
        });
    }

    /// <summary>
    /// 弹出新增设备模板对话框，从已连接设备创建模板并保存到数据库。
    /// </summary>
    private void OnAddTemplate()
    {
        _dialogService.ShowDialog("AddDeviceTemplateDialog", new DialogParameters(), async result =>
        {
            if (result.Result != ButtonResult.OK) return;

            var template = result.Parameters.GetValue<DeviceTemplate>("Template");
            if (template is null) return;

            try
            {
                await _templateRepository.SaveAsync(template);
                StatusText = $"已保存新模板: {template.Name}";
                _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                {
                    Title = "模板已保存",
                    Message = $"设备模板 [{template.Name}] 已保存到数据库",
                    Type = NotificationType.Success
                });

                // 刷新模板列表
                await LoadTemplatesAsync();
            }
            catch (Exception ex)
            {
                // 提取真正的内部报错信息
                string realErrorMessage = ex.InnerException != null
                    ? ex.InnerException.Message
                    : ex.Message;

                // 在控制台打印，或者记录到你的上位机 NLog/Serilog 日志中
                Console.WriteLine($"【数据库保存失败】根本原因: {realErrorMessage}");
                StatusText = $"保存模板失败: {ex.Message}";
                _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
                {
                    Title = "保存失败",
                    Message = ex.Message,
                    Type = NotificationType.Error
                });
            }
        });
    }
    /// <summary>
    /// 配置
    /// </summary>
    /// <param name="relativePath"></param>
    /// <returns></returns>
    private static string FindConfigDirectory(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir)!;
            if (dir is null) break;
        }
        // 不存在则在基目录创建
        return Path.Combine(AppContext.BaseDirectory, relativePath);
    }
}

/// <summary>
/// 设备模板列表项。
/// </summary>
public class DeviceTemplateItem
{
    public DeviceTemplate Template { get; }
    public string Name => Template.Name;
    public string TemplateId => Template.TemplateId;
    public string DriverType => Template.DriverType;
    public string Icon => Template.DriverType switch
    {
        "OpcUA" => "🔗",
        "Modbus" => "📡",
        "S7" => "🏭",
        _ => "⚙️"
    };
    public string Summary => $"{Template.DataPoints.Count} 个数据点";
    public string AlarmSummary
    {
        get
        {
            int count = Template.DataPoints.Count(dp => dp.AlarmTemplate is not null);
            return count > 0 ? $"{count} 个报警" : "无报警";
        }
    }
    public string TrendSummary
    {
        get
        {
            int count = Template.DataPoints.Count(dp => dp.TrendTemplate is not null);
            return count > 0 ? $"{count} 个趋势" : "无趋势";
        }
    }

    public DeviceTemplateItem(DeviceTemplate template)
    {
        Template = template;
    }
}

/// <summary>
/// 数据点显示项。
/// </summary>
public class DataPointDisplayItem
{
    public DataPointTemplate Template { get; }
    public string Name => Template.Name;
    public string TemplateId => Template.TemplateId;
    public string DataType => Template.DataType.ToString();
    public string Unit => Template.Unit;
    public string AlarmTemplateName => Template.AlarmTemplate?.Name ?? "—";
    public string TrendTemplateName => Template.TrendTemplate?.Name ?? "—";
    public bool HasAlarm => Template.AlarmTemplate is not null;
    public bool HasTrend => Template.TrendTemplate is not null;

    public DataPointDisplayItem(DataPointTemplate template)
    {
        Template = template;
    }
}

/// <summary>
/// 报警阈值显示项。
/// </summary>
public class AlarmThresholdItem
{
    public string Label { get; }
    public double Value { get; }
    public string Color { get; }

    public AlarmThresholdItem(string label, double value, string color)
    {
        Label = label;
        Value = value;
        Color = color;
    }
}
