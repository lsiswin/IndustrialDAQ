// File: TrendViewModel.cs  Module: UI (ViewModels)  Author: IndustrialDAQ Team
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using IndustrialDAQ.Acquisition;
using IndustrialDAQ.Alarm;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Core.ResourceTree;
using IndustrialDAQ.Infrastructure;
using IndustrialDAQ.Trend;
using IndustrialDAQ.UI.Models;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.EntityFrameworkCore;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation;
using SkiaSharp;
using Microsoft.Win32;
using System.Text;
using System.IO;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>
/// 趋势页面 ViewModel — 支持实时/历史模式、多 Tag 选择、报警线、暂停。
/// 实时模式：从 TrendEngine 内存缓存读取；历史模式：从 SQLite 查询。
/// </summary>
public class TrendViewModel : BindableBase, IDestructible
{
    private readonly TrendEngine _trendEngine;
    private readonly AlarmManager _alarmManager;
    private readonly IDbContextFactory<DaqDbContext> _dbFactory;
    private readonly IResourceTreeService _resourceTreeService;
    private readonly AcquisitionHost _acquisitionHost;
    private readonly DispatcherTimer _connectionTimer;

    // 颜色池
    private static readonly string[] Colors =
        ["#3B82F6", "#10B981", "#F59E0B", "#EF4444", "#8B5CF6", "#EC4899", "#06B6D4", "#84CC16"];

    // ─── LiveCharts 绑定 ───

    /// <summary>趋势曲线系列。</summary>
    public ObservableCollection<ISeries> Series { get; } = [];

    /// <summary>报警线系列（水平线）。</summary>
    public ObservableCollection<ISeries> AlarmLineSeries { get; } = [];

    /// <summary>X 轴配置（时间轴）。</summary>
    public Axis[] XAxes { get; }

    /// <summary>Y 轴配置（支持双轴）。</summary>
    public Axis[] YAxes { get; }

    /// <summary>图例画刷。</summary>
    public SolidColorPaint LegendPaint { get; }

    // ─── Tag 选择 ───

    /// <summary>可选 Tag 列表。</summary>
    public ObservableCollection<TrendTagItem> AvailableTags { get; } = [];

    /// <summary>当前已显示曲线的数据点，用于右侧常驻图例。</summary>
    public ObservableCollection<TrendTagItem> SelectedTags { get; } = [];

    /// <summary>按资源路径分组后的设备列表。</summary>
    public ObservableCollection<TrendDeviceItem> AvailableDevices { get; } = [];

    private TrendDeviceItem? _selectedDevice;
    /// <summary>复合多选框当前浏览的设备。</summary>
    public TrendDeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                RaisePropertyChanged(nameof(SelectedTagSummary));
                UpdateConnectionWarning();
            }
        }
    }

    private string _connectionWarningText = string.Empty;
    /// <summary>当前选择涉及的离线设备提醒。</summary>
    public string ConnectionWarningText
    {
        get => _connectionWarningText;
        set
        {
            if (SetProperty(ref _connectionWarningText, value))
                RaisePropertyChanged(nameof(HasConnectionWarning));
        }
    }

    /// <summary>是否需要显示设备离线提醒。</summary>
    public bool HasConnectionWarning => !string.IsNullOrWhiteSpace(ConnectionWarningText);

    /// <summary>当前已选择的趋势点位数量。</summary>
    public string SelectedTagSummary =>
        $"选择数据点 ({AvailableTags.Count(t => t.IsSelected)}/{AvailableTags.Count})";

    // ─── 控制 ───

    private bool _isPaused;
    /// <summary>是否暂停趋势。</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set { if (SetProperty(ref _isPaused, value)) RaisePropertyChanged(nameof(PauseButtonText)); }
    }

    /// <summary>暂停按钮文本。</summary>
    public string PauseButtonText => IsPaused ? "▶ 恢复" : "⏸ 暂停";

    private bool _isRealTimeMode = true;
    /// <summary>是否为实时模式（false = 历史模式）。</summary>
    public bool IsRealTimeMode
    {
        get => _isRealTimeMode;
        set { if (SetProperty(ref _isRealTimeMode, value)) { RaisePropertyChanged(nameof(ModeButtonText)); RaisePropertyChanged(nameof(IsHistoryMode)); } }
    }

    /// <summary>是否为历史模式（用于 UI 可见性绑定）。</summary>
    public bool IsHistoryMode => !_isRealTimeMode;

    /// <summary>模式切换按钮文本。</summary>
    public string ModeButtonText => IsRealTimeMode ? "🕐 历史模式" : "🔴 实时模式";

    private string _statusText = "实时模式";
    /// <summary>状态栏文本。</summary>
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    // ─── 历史查询参数 ───

    private DateTime _historyStart = DateTime.Now.AddHours(-1);
    /// <summary>历史查询起始时间。</summary>
    public DateTime HistoryStart { get => _historyStart; set => SetProperty(ref _historyStart, value); }

    private DateTime _historyEnd = DateTime.Now;
    /// <summary>历史查询结束时间。</summary>
    public DateTime HistoryEnd { get => _historyEnd; set => SetProperty(ref _historyEnd, value); }

    // ─── 数据统计 ───

    private string _statsText = "---";
    /// <summary>数据统计文本（最大/最小/平均/点数）。</summary>
    public string StatsText { get => _statsText; set => SetProperty(ref _statsText, value); }

    private int _totalPoints;
    /// <summary>当前图表总数据点数。</summary>
    public int TotalPoints { get => _totalPoints; set => SetProperty(ref _totalPoints, value); }

    // ─── 命令 ───

    public DelegateCommand TogglePauseCommand { get; }
    public DelegateCommand SwitchModeCommand { get; }
    public DelegateCommand<TrendTagItem> ToggleTagCommand { get; }
    public DelegateCommand QueryHistoryCommand { get; }
    public DelegateCommand SetLast15MinCommand { get; }
    public DelegateCommand SetLastHourCommand { get; }
    public DelegateCommand SetLast4HourCommand { get; }
    public DelegateCommand SetLast24HourCommand { get; }
    public DelegateCommand ExportHistoryCommand { get; }

    // 内部 Series 映射
    private readonly Dictionary<string, LineSeries<ObservablePoint>> _seriesMap = [];
    private readonly Dictionary<string, ISeries> _alarmMarkerSeriesMap = [];
    private int _defaultSelectionCount;

    public TrendViewModel(
        TrendEngine trendEngine,
        AlarmManager alarmManager,
        IDbContextFactory<DaqDbContext> dbFactory,
        IResourceTreeService resourceTreeService,
        AcquisitionHost acquisitionHost)
    {
        _trendEngine = trendEngine;
        _alarmManager = alarmManager;
        _dbFactory = dbFactory;
        _resourceTreeService = resourceTreeService;
        _acquisitionHost = acquisitionHost;

        // 配置 X 轴（时间轴）
        var typeface = SKTypeface.FromFamilyName("Microsoft YaHei",
            SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            ?? SKTypeface.Default;
        var darkText = new SolidColorPaint(new SKColor(0x9C, 0xA3, 0xAF)) { SKTypeface = typeface };
        var darkSeparator = new SolidColorPaint(new SKColor(0x37, 0x41, 0x51)) { StrokeThickness = 0.5f };
        LegendPaint = darkText;

        XAxes =
        [
            new Axis
            {
                Name = "时间",
                NameTextSize = 12,
                NamePaint = darkText,
                LabelsPaint = darkText,
                SeparatorsPaint = darkSeparator,
                TextSize = 10,
                Labeler = value =>
                {
                    // 防止 Ticks 越界：只在有效范围内转换
                    try
                    {
                        long ticks = (long)value;
                        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                            return string.Empty;
                        return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss");
                    }
                    catch
                    {
                        return string.Empty;
                    }
                },
                UnitWidth = TimeSpan.FromSeconds(1).Ticks,
                MinStep = TimeSpan.FromSeconds(1).Ticks
            }
        ];

        YAxes =
        [
            new Axis
            {
                NameTextSize = 12,
                NamePaint = darkText,
                LabelsPaint = darkText,
                SeparatorsPaint = darkSeparator,
                TextSize = 10,
                Position = LiveChartsCore.Measure.AxisPosition.Start
            }
        ];

        // 命令
        TogglePauseCommand = new DelegateCommand(() =>
        {
            if (!IsRealTimeMode) return; // 历史模式不支持暂停
            IsPaused = !IsPaused;
            StatusText = IsPaused ? "已暂停" : "实时模式";
        });

        SwitchModeCommand = new DelegateCommand(() =>
        {
            IsRealTimeMode = !IsRealTimeMode;
            IsPaused = false;

            if (IsRealTimeMode)
            {
                StatusText = "实时模式";
                // 切回实时模式时清除历史模式留下的坐标范围和缩放状态
                XAxes[0].MinLimit = null;
                XAxes[0].MaxLimit = null;
                YAxes[0].MinLimit = null;
                YAxes[0].MaxLimit = null;
                // 切回实时模式立刻刷新一次
                RefreshRealTimeData();
            }
            else
            {
                StatusText = "历史模式 — 请设置时间范围后点击「查询」";
                HistoryEnd = DateTime.Now;
                HistoryStart = HistoryEnd.AddHours(-1);
            }

            UpdateConnectionWarning();
            RefreshAlarmAnnotations();
        });

        ToggleTagCommand = new DelegateCommand<TrendTagItem>(item =>
        {
            if (item is null) return;
            // CheckBox 已通过 TwoWay 绑定更新状态，这里只同步曲线可见性，避免状态被反转两次。
            UpdateSeriesVisibility(item);
            RaisePropertyChanged(nameof(SelectedTagSummary));
            UpdateConnectionWarning();
        });

        QueryHistoryCommand = new DelegateCommand(async () => await QueryHistoryAsync());
        ExportHistoryCommand = new DelegateCommand(async () => await ExportHistoryAsync());

        SetLast15MinCommand = new DelegateCommand(() =>
        {
            HistoryEnd = DateTime.Now;
            HistoryStart = HistoryEnd.AddMinutes(-15);
            _ = QueryHistoryAsync();
        });
        SetLastHourCommand = new DelegateCommand(() =>
        {
            HistoryEnd = DateTime.Now;
            HistoryStart = HistoryEnd.AddHours(-1);
            _ = QueryHistoryAsync();
        });
        SetLast4HourCommand = new DelegateCommand(() =>
        {
            HistoryEnd = DateTime.Now;
            HistoryStart = HistoryEnd.AddHours(-4);
            _ = QueryHistoryAsync();
        });
        SetLast24HourCommand = new DelegateCommand(() =>
        {
            HistoryEnd = DateTime.Now;
            HistoryStart = HistoryEnd.AddHours(-24);
            _ = QueryHistoryAsync();
        });

        // 订阅数据刷新事件
        _trendEngine.DataRefreshed += OnDataRefreshed;
        _trendEngine.TagsChanged += OnTagsChanged;
        _alarmManager.AlarmTriggered += OnAlarmTriggered;
        _alarmManager.ActiveAlarmsChanged += OnActiveAlarmsChanged;

        // 即使设备掉线后不再产生采集事件，也要定时刷新连接提醒。
        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connectionTimer.Tick += OnConnectionTimerTick;
        _connectionTimer.Start();

        // 加载可用 Tag
        LoadAvailableTags();
    }

    private async Task ExportHistoryAsync()
    {
        var selectedIds = AvailableTags.Where(tag => tag.IsSelected).Select(tag => tag.TagId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedIds.Count == 0) { StatusText = "请先选择需要导出的数据点"; return; }
        var dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = $"IndustrialDAQ-History-{DateTime.Now:yyyyMMdd-HHmmss}.csv", AddExtension = true };
        if (dialog.ShowDialog() != true) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.HistoricalRecords.AsNoTracking().Where(row => selectedIds.Contains(row.TagId)).OrderBy(row => row.Timestamp).ToListAsync();
        var startUtc = HistoryStart.ToUniversalTime();
        var endUtc = HistoryEnd.ToUniversalTime();
        var builder = new StringBuilder("TagId,TagName,Timestamp,Value,ValueType,QualityCode\r\n");
        var count = 0;
        foreach (var row in rows)
        {
            if (!DateTimeOffset.TryParse(row.Timestamp, out var timestamp) || timestamp < startUtc || timestamp > endUtc) continue;
            builder.Append(Csv(row.TagId)).Append(',').Append(Csv(row.TagName)).Append(',').Append(Csv(timestamp.ToLocalTime().ToString("O"))).Append(',')
                .Append(Csv(row.Value)).Append(',').Append(Csv(row.ValueType)).Append(',').Append(row.QualityCode).Append("\r\n");
            count++;
        }
        await File.WriteAllTextAsync(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
        StatusText = $"已导出 {count} 条历史数据：{dialog.FileName}";
    }

    private static string Csv(string? value) => '"' + (value ?? string.Empty).Replace("\"", "\"\"") + '"';

    /// <inheritdoc />
    public void Destroy()
    {
        _trendEngine.DataRefreshed -= OnDataRefreshed;
        _trendEngine.TagsChanged -= OnTagsChanged;
        _alarmManager.AlarmTriggered -= OnAlarmTriggered;
        _alarmManager.ActiveAlarmsChanged -= OnActiveAlarmsChanged;
        _connectionTimer.Stop();
        _connectionTimer.Tick -= OnConnectionTimerTick;
    }

    /// <summary>
    /// 加载可选 Tag 列表并创建 Series。
    /// </summary>
    private void LoadAvailableTags()
    {
        SyncAvailableTags();

        // 加载报警线
        LoadAlarmLines();
    }

    /// <summary>
    /// 加载报警线到图表（使用固定 X 范围，避免越界）。
    /// </summary>
    private void LoadAlarmLines()
    {
        foreach (var line in _trendEngine.AlarmLines)
        {
            AlarmLineSeries.Add(new LineSeries<ObservablePoint>
            {
                Name = line.Label,
                Values = new ObservableCollection<ObservablePoint>
                {
                    new(DateTime.UtcNow.AddHours(-24).Ticks, line.Value),
                    new(DateTime.UtcNow.AddHours(1).Ticks, line.Value)
                },
                Stroke = new SolidColorPaint(SKColor.Parse(line.Color)) { StrokeThickness = 1 },
                Fill = null,
                GeometrySize = 0,
                ScalesYAt = 0
            });
        }
    }

    /// <summary>
    /// 数据刷新回调 — 实时模式下从 TrendCache 读取数据更新 Series。
    /// </summary>
    private void OnDataRefreshed()
    {
        if (IsPaused || !IsRealTimeMode) return;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            // 点位可能在页面创建后才由运行时配置注册，先同步列表再刷新曲线。
            SyncAvailableTags();
            RefreshRealTimeData();
        });
    }

    /// <summary>趋势配置热更新时同步新增点位，保留已有点位的勾选状态。</summary>
    private void OnTagsChanged()
    {
        Application.Current?.Dispatcher.Invoke(SyncAvailableTags);
    }

    private void SyncAvailableTags()
    {
        var trackedTagIds = _trendEngine.DataStore.TrackedTagIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = _resourceTreeService.Current;
        var deviceById = snapshot.Nodes
            .Where(node => node.IsEnabled && node.ResourceType == ResourceType.Device)
            .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);

        int colorIdx = AvailableTags.Count;
        var usedColors = AvailableTags
            .Select(item => item.Color)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tagNode in snapshot.Nodes
                     .Where(node => node.IsEnabled && node.ResourceType == ResourceType.Tag)
                     .OrderBy(node => node.SortOrder))
        {
            if (!TryReadTagMetadata(tagNode.MetadataJson, out var tagId, out var dataType)) continue;
            if (!trackedTagIds.Contains(tagId) || !IsNumericDataType(dataType)) continue;
            if (AvailableTags.Any(item => item.TagId == tagId)) continue;
            if (tagNode.ParentId is null || !deviceById.TryGetValue(tagNode.ParentId, out var deviceNode)) continue;

            var template = _trendEngine.DataStore.GetTemplate(tagId);
            string color = template?.LineColor ?? Colors[colorIdx++ % Colors.Length];
            // 模板可能沿用相同默认色；趋势页自动换成未使用颜色，保证曲线和常驻图例易于区分。
            if (!usedColors.Add(color))
            {
                color = Colors.FirstOrDefault(candidate => usedColors.Add(candidate))
                        ?? Colors[colorIdx++ % Colors.Length];
            }
            string name = template?.Name ?? tagNode.DisplayName;
            var tagItem = new TrendTagItem
            {
                DeviceId = deviceNode.Id,
                DeviceName = deviceNode.DisplayName,
                ResourcePath = tagNode.Path.Value,
                TagId = tagId,
                TagName = name,
                Unit = template?.Unit ?? string.Empty,
                Color = color,
                IsSelected = false
            };
            AvailableTags.Add(tagItem);

            var deviceItem = AvailableDevices.FirstOrDefault(item => item.DeviceId == deviceNode.Id);
            if (deviceItem is null)
            {
                deviceItem = new TrendDeviceItem
                {
                    DeviceId = deviceNode.Id,
                    DeviceName = deviceNode.DisplayName,
                    ResourcePath = deviceNode.Path.Value
                };
                AvailableDevices.Add(deviceItem);
            }
            deviceItem.Tags.Add(tagItem);

            var strokePaint = new SolidColorPaint(SKColor.Parse(color))
            {
                StrokeThickness = (float)(template?.StrokeThickness ?? 2)
            };
            var series = new LineSeries<ObservablePoint>
            {
                Name = name,
                Values = new ObservableCollection<ObservablePoint>(),
                // 使用透明几何点扩大命中区域，使实时和历史模式的普通数据点都能触发 Tooltip。
                GeometrySize = template?.ShowGeometry == true ? 6 : 8,
                Stroke = strokePaint,
                GeometryStroke = template?.ShowGeometry == true
                    ? new SolidColorPaint(SKColor.Parse(color)) { StrokeThickness = 2 }
                    : new SolidColorPaint(SKColors.Transparent),
                GeometryFill = template?.ShowGeometry == true
                    ? new SolidColorPaint(SKColor.Parse(color))
                    : new SolidColorPaint(SKColors.Transparent),
                Fill = null,
                LineSmoothness = 0,
                YToolTipLabelFormatter = point => FormatPointTooltip(
                    name,
                    deviceNode.DisplayName,
                    template?.Unit ?? string.Empty,
                    point.Coordinate.SecondaryValue,
                    point.Coordinate.PrimaryValue)
            };
            _seriesMap[tagId] = series;
        }

        ApplyDefaultSelection();

        // 默认定位到包含已选点位的设备，便于进入页面后立即看到当前选择。
        SelectedDevice ??= AvailableDevices.FirstOrDefault(device => device.Tags.Any(tag => tag.IsSelected))
                           ?? AvailableDevices.FirstOrDefault();
        RaisePropertyChanged(nameof(SelectedTagSummary));
        UpdateConnectionWarning();
        RefreshAlarmAnnotations();

        // 点位首次出现后立即刷新一次，避免等待下一轮 UI 事件造成空图。
        if (AvailableTags.Count > 0 && IsRealTimeMode && !IsPaused)
            RefreshRealTimeData();
    }

    /// <summary>读取资源节点元数据中的 TagId 与数据类型。</summary>
    private static bool TryReadTagMetadata(string? metadataJson, out string tagId, out TagDataType dataType)
    {
        tagId = string.Empty;
        dataType = default;
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("tagId", out var tagIdElement) ||
                !root.TryGetProperty("dataType", out var dataTypeElement))
                return false;

            tagId = tagIdElement.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(tagId) &&
                   Enum.TryParse(dataTypeElement.GetString(), true, out dataType);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>趋势图只接受可转换为连续数值的 Tag，显式排除 Bool 与 String。</summary>
    private static bool IsNumericDataType(TagDataType dataType) =>
        dataType is TagDataType.Int16 or TagDataType.Int32 or TagDataType.Int64 or
            TagDataType.UInt16 or TagDataType.UInt32 or TagDataType.Float32 or TagDataType.Float64;

    /// <summary>统一格式化普通趋势点 Tooltip，避免直接显示 DateTime Ticks 的科学计数法。</summary>
    private static string FormatPointTooltip(
        string tagName,
        string deviceName,
        string unit,
        double timestampTicks,
        double value)
    {
        var ticks = (long)timestampTicks;
        var timeText = ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "未知时间";
        var unitSuffix = string.IsNullOrWhiteSpace(unit) ? string.Empty : $" {unit}";
        return $"{tagName}\n设备：{deviceName}\n时间：{timeText}\n数值：{value:F2}{unitSuffix}";
    }

    /// <summary>
    /// 默认显示实际液位和实际速度，优先选择已连接设备；设定值 SetLevel/SetSpeed 不再默认显示。
    /// </summary>
    private void ApplyDefaultSelection()
    {
        if (_defaultSelectionCount > 0 || AvailableTags.Any(tag => tag.IsSelected)) return;

        var preferredNames = new[] { "Filling.ActualLevel", "Conveyor.ActualSpeed" };
        var defaults = preferredNames
            .Select(name => AvailableTags
                .Where(tag => string.Equals(tag.TagName, name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(tag => _acquisitionHost.IsDeviceConnected(tag.DeviceId))
                .FirstOrDefault())
            .Where(tag => tag is not null)
            .Cast<TrendTagItem>()
            .DistinctBy(tag => tag.TagId)
            .Take(2)
            .ToList();

        foreach (var tag in defaults)
        {
            tag.IsSelected = true;
            if (_seriesMap.TryGetValue(tag.TagId, out var series)) Series.Add(series);
            SelectedTags.Add(tag);
        }

        _defaultSelectionCount = defaults.Count;
    }

    private void OnConnectionTimerTick(object? sender, EventArgs e) => UpdateConnectionWarning();

    /// <summary>
    /// 检查当前浏览设备以及所有已选曲线所属设备，任一掉线都会给出明确提醒。
    /// 历史模式读取数据库，不阻止查询，但仍提示当前设备实时连接状态。
    /// </summary>
    private void UpdateConnectionWarning()
    {
        var involvedDevices = AvailableTags
            .Where(tag => tag.IsSelected)
            .Select(tag => new { tag.DeviceId, tag.DeviceName })
            .ToList();

        if (SelectedDevice is not null &&
            involvedDevices.All(device => !string.Equals(device.DeviceId, SelectedDevice.DeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            involvedDevices.Add(new { SelectedDevice.DeviceId, SelectedDevice.DeviceName });
        }

        var disconnectedNames = involvedDevices
            .Where(device => !_acquisitionHost.IsDeviceConnected(device.DeviceId))
            .Select(device => device.DeviceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ConnectionWarningText = disconnectedNames.Count == 0
            ? string.Empty
            : $"设备未连接或已掉线：{string.Join("、", disconnectedNames)}。实时曲线可能停止更新，历史数据仍可查询。";
    }

    /// <summary>
    /// 从内存缓存刷新实时数据到图表。
    /// </summary>
    private void RefreshRealTimeData()
    {
        int totalPts = 0;
        double globalMin = double.MaxValue, globalMax = double.MinValue, globalSum = 0;
        int globalCount = 0;
        long firstTimestamp = long.MaxValue;
        long lastTimestamp = long.MinValue;

        foreach (var tagItem in AvailableTags)
        {
            if (!tagItem.IsSelected) continue;
            if (!_seriesMap.TryGetValue(tagItem.TagId, out var series)) continue;
            if (series.Values is not ObservableCollection<ObservablePoint> values) continue;

            var cache = _trendEngine.DataStore.GetCache(tagItem.TagId);
            if (cache is null) continue;

            var template = _trendEngine.DataStore.GetTemplate(tagItem.TagId);
            int windowSeconds = template?.WindowSeconds ?? 300;

            var points = cache.GetWindow(windowSeconds);
            if (points.Length == 0) continue;

            values.Clear();
            foreach (var p in points)
            {
                // 只使用有效 UTC 时间的 Ticks
                long ticks = p.Timestamp.ToUniversalTime().Ticks;
                values.Add(new ObservablePoint(ticks, p.Value));
                firstTimestamp = Math.Min(firstTimestamp, ticks);
                lastTimestamp = Math.Max(lastTimestamp, ticks);
                totalPts++;

                // 统计
                if (p.Value < globalMin) globalMin = p.Value;
                if (p.Value > globalMax) globalMax = p.Value;
                globalSum += p.Value;
                globalCount++;
            }
        }

        TotalPoints = totalPts;
        UpdateStats(globalMin, globalMax, globalSum, globalCount);
        // 报警持续期间每次刷新都补充最新红色采样点，而不是只保留触发瞬间。
        RefreshAlarmAnnotations();

        if (globalCount > 0 && globalMax > globalMin)
        {
            double padding = Math.Max((globalMax - globalMin) * 0.08, 0.5);
            var yMin = globalMin - padding;
            var yMax = globalMax + padding;
            // 仅在范围有效时设置 Y 轴，避免 LiveChartsCore 因 MinLimit >= MaxLimit 隐藏坐标轴
            if (yMin < yMax)
            {
                YAxes[0].MinLimit = yMin;
                YAxes[0].MaxLimit = yMax;
            }
            // X 轴（时间）交给 LiveCharts 自管理，不写死限制，避免缩放/交互时坐标轴消失
            XAxes[0].MinLimit = null;
            XAxes[0].MaxLimit = null;
        }

        StatusText = IsPaused ? "已暂停" :
            $"实时模式 | {AvailableTags.Count(t => t.IsSelected)} 个 Tag | {totalPts} 个点 | {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>
    /// 历史模式：从 SQLite 查询 HistoricalRecords。
    /// </summary>
    private async Task QueryHistoryAsync()
    {
        if (HistoryStart >= HistoryEnd)
        {
            StatusText = "错误：起始时间必须早于结束时间";
            return;
        }

        StatusText = "正在查询历史数据...";
        var selectedTagIds = AvailableTags
            .Where(t => t.IsSelected)
            .Select(t => t.TagId)
            .ToList();

        if (selectedTagIds.Count == 0)
        {
            StatusText = "请至少选择一个 Tag";
            return;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // 按 TagId 分批查询，最多返回每个 Tag 5000 条（防止内存溢出）
            var queryResults = new Dictionary<string, List<(DateTime Ts, double Val)>>();
            foreach (var tagId in selectedTagIds)
            {
                // SQLite 无法翻译 string.Compare；先按 TagId 查询，再在内存中解析 ISO 时间过滤。
                var rows = await db.HistoricalRecords
                    .AsNoTracking()
                    .Where(r => r.TagId == tagId)
                    .OrderByDescending(r => r.Timestamp)
                    .Take(10000)
                    .Select(r => new { r.Timestamp, r.Value, r.ValueType })
                    .ToListAsync();

                var pts = new List<(DateTime, double)>();
                foreach (var row in rows)
                {
                    if (!DateTimeOffset.TryParse(row.Timestamp, out var dto)) continue;
                    if (dto < HistoryStart.ToUniversalTime() || dto > HistoryEnd.ToUniversalTime()) continue;
                    if (!HistoricalValueCodec.TryGetDouble(row.Value, row.ValueType, out double v)) continue;
                    pts.Add((dto.UtcDateTime, v));
                }
                pts.Sort(static (left, right) => left.Item1.CompareTo(right.Item1));
                queryResults[tagId] = pts;
            }

            // 在 UI 线程更新图表
            Application.Current?.Dispatcher.Invoke(() =>
            {
                int totalPts = 0;
                double globalMin = double.MaxValue, globalMax = double.MinValue, globalSum = 0;
                int globalCount = 0;

                foreach (var tagItem in AvailableTags)
                {
                    if (!_seriesMap.TryGetValue(tagItem.TagId, out var series)) continue;
                    if (series.Values is not ObservableCollection<ObservablePoint> values) continue;

                    values.Clear();
                    if (!queryResults.TryGetValue(tagItem.TagId, out var pts)) continue;

                    foreach (var (ts, val) in pts)
                    {
                        values.Add(new ObservablePoint(ts.Ticks, val));
                        totalPts++;
                        if (val < globalMin) globalMin = val;
                        if (val > globalMax) globalMax = val;
                        globalSum += val;
                        globalCount++;
                    }
                }

                TotalPoints = totalPts;
                UpdateStats(globalMin, globalMax, globalSum, globalCount);

                StatusText = totalPts == 0
                    ? $"历史模式 | 该时间段内无数据 ({HistoryStart:MM-dd HH:mm} ~ {HistoryEnd:MM-dd HH:mm})"
                    : $"历史模式 | {totalPts} 个点 | {HistoryStart:MM-dd HH:mm} ~ {HistoryEnd:MM-dd HH:mm}";
            });
        }
        catch (Exception ex)
        {
            StatusText = $"查询失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 更新统计信息文本。
    /// </summary>
    private void UpdateStats(double min, double max, double sum, int count)
    {
        if (count == 0)
        {
            StatsText = "暂无数据";
            return;
        }

        double avg = sum / count;
        StatsText = $"最大: {max:F2}  最小: {min:F2}  均值: {avg:F2}  总点数: {count}";
    }

    /// <summary>
    /// 报警触发事件 — 更新状态栏显示最新报警。
    /// </summary>
    private void OnAlarmTriggered(object? sender, AlarmEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusText = $"⚠ 报警: {e.Record.Title} — {e.Record.TagName} = {e.Record.TriggerValue:F1}";
        });
    }

    private void OnActiveAlarmsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.Invoke(RefreshAlarmAnnotations);

    /// <summary>
    /// 将活跃报警映射为独立的红色散点覆盖层。
    /// 基础趋势线保持原色，报警描述只写入红色散点的 Series.Name，
    /// 因此只有鼠标悬浮到报警点时才会显示报警原因。
    /// </summary>
    private void RefreshAlarmAnnotations()
    {
        var alarmsByTag = _alarmManager.GetActiveAlarms()
            .Where(alarm => !string.IsNullOrWhiteSpace(alarm.TagId))
            .GroupBy(alarm => alarm.TagId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var tag in AvailableTags)
        {
            // 先移除旧覆盖层，避免报警更新后残留重复红点。
            if (_alarmMarkerSeriesMap.Remove(tag.TagId, out var oldMarkerSeries))
                Series.Remove(oldMarkerSeries);

            tag.IsAlarmActive = alarmsByTag.TryGetValue(tag.TagId, out var alarms);
            var activeAlarms = alarms ?? [];
            tag.AlarmDescription = tag.IsAlarmActive
                ? string.Join("；", activeAlarms.Select(alarm => $"{alarm.Title}：{alarm.Message}"))
                : string.Empty;

            if (!tag.IsAlarmActive || !tag.IsSelected || !IsRealTimeMode) continue;

            var alarmStartTicks = activeAlarms.Min(alarm => alarm.OccurredAt.ToUniversalTime().Ticks);
            var alarmPoints = _seriesMap.TryGetValue(tag.TagId, out var baseSeries) &&
                              baseSeries.Values is ObservableCollection<ObservablePoint> baseValues
                ? baseValues
                    .Where(point => point.X is not null && point.X.Value >= alarmStartTicks)
                    .Select(point => new ObservablePoint(point.X, point.Y))
                    .ToList()
                : [];

            // 触发事件可能早于缓存刷新，至少保留报警触发点供用户立即查看。
            if (alarmPoints.Count == 0)
            {
                alarmPoints.AddRange(activeAlarms.Select(alarm =>
                    new ObservablePoint(alarm.OccurredAt.ToUniversalTime().Ticks, alarm.TriggerValue)));
            }
            var alarmDescription = string.Join("；", activeAlarms.Select(alarm => $"{alarm.Title}：{alarm.Message}"));
            var alarmPaint = new SolidColorPaint(SKColor.Parse("#EF4444"));
            var markerSeries = new ScatterSeries<ObservablePoint>
            {
                Name = $"{tag.TagName} 报警",
                Values = new ObservableCollection<ObservablePoint>(alarmPoints),
                GeometrySize = 13,
                Fill = alarmPaint,
                Stroke = new SolidColorPaint(SKColor.Parse("#EF4444")) { StrokeThickness = 2 },
                YToolTipLabelFormatter = point =>
                    $"{FormatPointTooltip(tag.TagName, tag.DeviceName, tag.Unit, point.Coordinate.SecondaryValue, point.Coordinate.PrimaryValue)}\n报警：{alarmDescription}"
            };

            _alarmMarkerSeriesMap[tag.TagId] = markerSeries;
            Series.Add(markerSeries);
        }
    }

    /// <summary>
    /// 更新 Series 可见性。
    /// </summary>
    private void UpdateSeriesVisibility(TrendTagItem item)
    {
        if (_seriesMap.TryGetValue(item.TagId, out var series))
        {
            if (item.IsSelected)
            {
                if (!Series.Contains(series))
                    Series.Add(series);
                if (!SelectedTags.Contains(item))
                    SelectedTags.Add(item);
                RefreshAlarmAnnotations();
            }
            else
            {
                // 直接移除曲线，避免 LiveCharts 仅更新 IsVisible 后仍保留旧线条。
                Series.Remove(series);
                SelectedTags.Remove(item);
                if (_alarmMarkerSeriesMap.Remove(item.TagId, out var alarmMarkerSeries))
                    Series.Remove(alarmMarkerSeries);
                if (series.Values is ObservableCollection<ObservablePoint> values)
                    values.Clear();
            }
        }
    }
}
