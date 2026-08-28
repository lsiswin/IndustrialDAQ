using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Core.ResourceTree;
using IndustrialDAQ.Alarm.RuleEngine;
using IndustrialDAQ.UI.Events;
using IndustrialDAQ.UI.Services;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Navigation.Regions;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>
/// 报警规则统一配置 ViewModel。
/// 合并了原"报警规则向导"与"规则配置"两个页面的能力：
/// 向导式资源/条件构造 + 高级字段（RuleId / 自定义资源路径 / 自定义条件表达式）直接编辑，
/// 保存后自动热重载，并可手动触发"应用并热重载"。
/// 用户不直接维护底层 ConditionExpression 时由引擎按运算符自动生成。
/// </summary>
public class AlarmRuleConfigViewModel : BindableBase, INavigationAware
{
    private readonly IAlarmDefinitionRepository _repository;
    private readonly IAlarmDefinitionService _alarmDefinitionService;
    private readonly IResourceTreeService _resourceTreeService;
    private readonly IRuleEngineService _ruleEngineService;
    private readonly IEventAggregator _eventAggregator;
    private readonly IAuthManager _authManager;
    public bool CanModify => _authManager.CanModify;

    // ── 规则列表 ──
    public ObservableCollection<AlarmDefinition> Rules { get; } = new();

    private AlarmDefinition? _selectedRule;
    public AlarmDefinition? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (SetProperty(ref _selectedRule, value) && value != null)
            {
                LoadFromDefinition(value);
                IsEditMode = true;
            }
        }
    }

    private bool _isEditMode;
    public bool IsEditMode
    {
        get => _isEditMode;
        set => SetProperty(ref _isEditMode, value);
    }

    // ── 基本信息 ──
    private string _ruleId = string.Empty;
    public string RuleId
    {
        get => _ruleId;
        set => SetProperty(ref _ruleId, value);
    }

    private string _alarmCode = string.Empty;
    public string AlarmCode
    {
        get => _alarmCode;
        set => SetProperty(ref _alarmCode, value);
    }

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public IEnumerable<AlarmSeverity> Severities => Enum.GetValues<AlarmSeverity>();

    private AlarmSeverity _severity = AlarmSeverity.Warning;
    public AlarmSeverity Severity
    {
        get => _severity;
        set => SetProperty(ref _severity, value);
    }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    // ── 资源与数据点（向导式） ──
    public ObservableCollection<string> ResourcePaths { get; } = new();

    private string? _selectedResourcePath;
    public string? SelectedResourcePath
    {
        get => _selectedResourcePath;
        set
        {
            if (SetProperty(ref _selectedResourcePath, value))
            {
                UpdateAvailableTags(value);
                RaisePropertyChanged(nameof(CanSave));
            }
        }
    }

    public ObservableCollection<string> AvailableTagNames { get; } = new();

    private string? _selectedTagName;
    public string? SelectedTagName
    {
        get => _selectedTagName;
        set
        {
            if (SetProperty(ref _selectedTagName, value))
            {
                // 空数据点名称不能作为 ResourcePath 解析，避免保存后回填失败。
                if (!string.IsNullOrWhiteSpace(value))
                    _resolvedTagId = ResolveTagId(value);
                RaisePropertyChanged(nameof(CanSave));
                RaisePropertyChanged(nameof(PreviewSummary));
            }
        }
    }

    private string _resolvedTagId = string.Empty;

    // ── 高级：自定义资源路径 ──
    private bool _useCustomResourcePath;
    public bool UseCustomResourcePath
    {
        get => _useCustomResourcePath;
        set
        {
            if (SetProperty(ref _useCustomResourcePath, value))
            {
                RaisePropertyChanged(nameof(CanSave));
                RaisePropertyChanged(nameof(PreviewSummary));
            }
        }
    }

    private string? _customTargetResourcePath;
    public string? CustomTargetResourcePath
    {
        get => _customTargetResourcePath;
        set
        {
            if (SetProperty(ref _customTargetResourcePath, value))
            {
                RaisePropertyChanged(nameof(CanSave));
                RaisePropertyChanged(nameof(PreviewSummary));
            }
        }
    }

    private string? _customResourcePath;
    public string? CustomResourcePath
    {
        get => _customResourcePath;
        set => SetProperty(ref _customResourcePath, value);
    }

    private string? _customTagId;
    public string? CustomTagId
    {
        get => _customTagId;
        set => SetProperty(ref _customTagId, value);
    }

    // ── 触发条件（向导式） ──
    public IEnumerable<AlarmOperator> Operators => Enum.GetValues<AlarmOperator>()
        .Where(op => op != AlarmOperator.RateOfChange);

    private AlarmOperator _selectedOperator = AlarmOperator.GreaterThan;
    public AlarmOperator SelectedOperator
    {
        get => _selectedOperator;
        set
        {
            if (SetProperty(ref _selectedOperator, value))
                RaisePropertyChanged(nameof(PreviewExpression));
        }
    }

    private double _threshold;
    public double Threshold
    {
        get => _threshold;
        set
        {
            if (SetProperty(ref _threshold, value))
                RaisePropertyChanged(nameof(PreviewExpression));
        }
    }

    private double _deadband;
    public double Deadband
    {
        get => _deadband;
        set
        {
            if (SetProperty(ref _deadband, value))
                RaisePropertyChanged(nameof(PreviewExpression));
        }
    }

    // ── 高级：自定义条件表达式 ──
    private bool _useCustomCondition;
    public bool UseCustomCondition
    {
        get => _useCustomCondition;
        set
        {
            if (SetProperty(ref _useCustomCondition, value))
                RaisePropertyChanged(nameof(PreviewExpression));
        }
    }

    private string? _conditionExpression;
    public string? ConditionExpression
    {
        get => _conditionExpression;
        set
        {
            if (SetProperty(ref _conditionExpression, value))
                RaisePropertyChanged(nameof(PreviewExpression));
        }
    }

    // ── 报警属性 ──
    private int _delayMs;
    public int DelayMs
    {
        get => _delayMs;
        set => SetProperty(ref _delayMs, value);
    }

    private double _hysteresis;
    public double Hysteresis
    {
        get => _hysteresis;
        set => SetProperty(ref _hysteresis, value);
    }

    private int _cooldownSeconds = 60;
    public int CooldownSeconds
    {
        get => _cooldownSeconds;
        set => SetProperty(ref _cooldownSeconds, value);
    }

    private AlarmAckPolicy _ackPolicy = AlarmAckPolicy.Required;
    public AlarmAckPolicy AckPolicy
    {
        get => _ackPolicy;
        set => SetProperty(ref _ackPolicy, value);
    }

    public IEnumerable<AlarmAckPolicy> AckPolicies => Enum.GetValues<AlarmAckPolicy>();

    private string _messageTemplate = string.Empty;
    public string MessageTemplate
    {
        get => _messageTemplate;
        set => SetProperty(ref _messageTemplate, value);
    }

    // ── 预览 ──
    public string PreviewExpression => UseCustomCondition && !string.IsNullOrWhiteSpace(ConditionExpression)
        ? ConditionExpression!
        : GetOperatorDisplayText();

    public string PreviewSummary => $"规则 ID: {RuleId}\n" +
        $"资源: {TargetResourceDisplay}\n" +
        $"数据点: {SelectedTagName ?? "未选择"}\n" +
        $"条件: {PreviewExpression}\n" +
        $"级别: {Severity}\n" +
        $"延迟: {DelayMs} ms\n" +
        $"死区: {Hysteresis}\n" +
        $"冷却: {CooldownSeconds}s\n" +
        $"确认策略: {AckPolicy}";

    private string TargetResourceDisplay =>
        UseCustomResourcePath && !string.IsNullOrWhiteSpace(CustomTargetResourcePath)
            ? CustomTargetResourcePath!
            : SelectedResourcePath ?? "未选择";

    // ── 命令 ──
    public DelegateCommand CreateNewCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand DeleteCommand { get; }
    public DelegateCommand ReloadEngineCommand { get; }

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(AlarmCode) &&
        (!string.IsNullOrWhiteSpace(SelectedResourcePath) ||
         (UseCustomResourcePath && !string.IsNullOrWhiteSpace(CustomTargetResourcePath))) &&
        (!UseCustomCondition || !string.IsNullOrWhiteSpace(ConditionExpression));

    public AlarmRuleConfigViewModel(
        IAlarmDefinitionRepository repository,
        IAlarmDefinitionService alarmDefinitionService,
        IResourceTreeService resourceTreeService,
        IRuleEngineService ruleEngineService,
        IEventAggregator eventAggregator,
        IAuthManager authManager)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _alarmDefinitionService = alarmDefinitionService ?? throw new ArgumentNullException(nameof(alarmDefinitionService));
        _resourceTreeService = resourceTreeService ?? throw new ArgumentNullException(nameof(resourceTreeService));
        _ruleEngineService = ruleEngineService ?? throw new ArgumentNullException(nameof(ruleEngineService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _authManager = authManager ?? throw new ArgumentNullException(nameof(authManager));
        _authManager.CurrentUserChanged += (_, _) => RaisePropertyChanged(nameof(CanModify));

        CreateNewCommand = new DelegateCommand(OnCreateNew, () => CanModify);
        SaveCommand = new DelegateCommand(OnSaveExecute, () => CanModify && CanSave)
            .ObservesProperty(() => AlarmCode)
            .ObservesProperty(() => SelectedResourcePath)
            .ObservesProperty(() => UseCustomResourcePath)
            .ObservesProperty(() => CustomTargetResourcePath)
            .ObservesProperty(() => UseCustomCondition)
            .ObservesProperty(() => ConditionExpression);
        DeleteCommand = new DelegateCommand(OnDeleteExecute, () => CanModify && IsEditMode).ObservesProperty(() => IsEditMode);
        ReloadEngineCommand = new DelegateCommand(OnReloadEngineExecute, () => CanModify);
        _authManager.CurrentUserChanged += (_, _) =>
        {
            CreateNewCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            ReloadEngineCommand.RaiseCanExecuteChanged();
        };

        // 设备树热重载（运行时增删设备）后，自动刷新资源路径下拉框
        _eventAggregator.GetEvent<ConfigurationReloadedEvent>().Subscribe(OnConfigurationReloaded);
    }

    private void OnConfigurationReloaded()
    {
        _ = LoadResourceTreeAsync();
    }

    public async void OnNavigatedTo(NavigationContext navigationContext)
    {
        await LoadResourceTreeAsync();
        await LoadRulesAsync();
    }

    /// <summary>
    /// 资源树是按需从存储库异步加载的，Current 在首次加载前始终为 Empty。
    /// 导航进入本页面时务必先重载，否则资源路径/数据点下拉将为空。
    /// </summary>
    private async Task LoadResourceTreeAsync()
    {
        try
        {
            await _resourceTreeService.ReloadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            PublishNotification("资源树加载失败", ex.Message, NotificationType.Error);
        }

        LoadResourceTree();
    }

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;
    public void OnNavigatedFrom(NavigationContext navigationContext) { }

    private void LoadResourceTree()
    {
        var snapshot = _resourceTreeService.Current;
        ResourcePaths.Clear();

        foreach (var node in snapshot.Nodes.OrderBy(n => n.Path.Value))
        {
            if (node.ResourceType is ResourceType.Tag or ResourceType.Device)
                ResourcePaths.Add(node.Path.Value);
        }
    }

    private string ResolveTagId(string tagPath)
    {
        var snapshot = _resourceTreeService.Current;
        var node = snapshot.Find(ResourcePath.Parse(tagPath));
        if (node?.MetadataJson != null)
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(node.MetadataJson);
                if (doc.RootElement.TryGetProperty("tagId", out var tagIdEl))
                    return tagIdEl.GetString() ?? string.Empty;
            }
            catch { }
        }
        return string.Empty;
    }

    private void UpdateAvailableTags(string? resourcePath)
    {
        AvailableTagNames.Clear();
        SelectedTagName = null;
        _resolvedTagId = string.Empty;

        if (string.IsNullOrWhiteSpace(resourcePath))
            return;

        var snapshot = _resourceTreeService.Current;
        var node = snapshot.Find(ResourcePath.Parse(resourcePath));
        if (node == null)
            return;

        if (node.ResourceType == ResourceType.Device)
        {
            foreach (var child in snapshot.GetChildren(node.Path).OrderBy(c => c.SortOrder))
            {
                if (child.ResourceType == ResourceType.Tag && child.IsEnabled)
                    AvailableTagNames.Add(child.Path.Value);
            }
        }
        else if (node.ResourceType == ResourceType.Tag)
        {
            AvailableTagNames.Add(node.Path.Value);
            SelectedTagName = node.Path.Value;
        }
    }

    private void OnCreateNew()
    {
        SelectedRule = null;
        IsEditMode = false;
        RuleId = $"alm-{Guid.NewGuid():N}"[..16];
        SelectedResourcePath = null;
        SelectedTagName = null;
        _resolvedTagId = string.Empty;
        AvailableTagNames.Clear();
        SelectedOperator = AlarmOperator.GreaterThan;
        Threshold = 0;
        Deadband = 0;
        AlarmCode = string.Empty;
        Title = string.Empty;
        Severity = AlarmSeverity.Warning;
        DelayMs = 0;
        Hysteresis = 0;
        CooldownSeconds = 60;
        AckPolicy = AlarmAckPolicy.Required;
        MessageTemplate = string.Empty;
        IsEnabled = true;
        UseCustomCondition = false;
        ConditionExpression = null;
        UseCustomResourcePath = false;
        CustomResourcePath = null;
        CustomTargetResourcePath = null;
        CustomTagId = null;
    }

    private async void OnSaveExecute()
    {
        if (!CanSave)
        {
            PublishNotification("验证失败", "请填写报警代码与资源路径（或自定义资源路径）。", NotificationType.Warning);
            return;
        }

        try
        {
            var targetPath = UseCustomResourcePath && !string.IsNullOrWhiteSpace(CustomTargetResourcePath)
                ? (ResourcePath?)ResourcePath.Parse(CustomTargetResourcePath!)
                : (SelectedResourcePath != null ? (ResourcePath?)ResourcePath.Parse(SelectedResourcePath) : null);

            var tagId = UseCustomResourcePath && !string.IsNullOrWhiteSpace(CustomTagId)
                ? CustomTagId!
                : _resolvedTagId;

            var alarmPath = UseCustomResourcePath && !string.IsNullOrWhiteSpace(CustomResourcePath)
                ? (ResourcePath?)ResourcePath.Parse(CustomResourcePath!)
                : (targetPath != null && !string.IsNullOrWhiteSpace(AlarmCode)
                    ? (ResourcePath?)ResourcePath.Parse($"{targetPath}/Alarm/{AlarmCode}")
                    : null);

            // 自定义路径模式没有下拉数据点名称，统一从目标资源路径末段推导。
            var resolvedTagName = !string.IsNullOrWhiteSpace(SelectedTagName)
                ? SelectedTagName.Split('/').Last()
                : targetPath?.Value.Split('/').LastOrDefault() ?? tagId;

            var condition = UseCustomCondition && !string.IsNullOrWhiteSpace(ConditionExpression)
                ? ConditionExpression!
                : string.Empty;

            var def = new AlarmDefinition
            {
                Id = IsEditMode && SelectedRule != null ? SelectedRule.Id : Guid.NewGuid().ToString("N"),
                RuleId = IsEditMode && SelectedRule != null ? SelectedRule.RuleId : RuleId,
                AlarmCode = AlarmCode,
                ResourcePath = alarmPath,
                TargetResourcePath = targetPath,
                TagId = tagId,
                TagName = resolvedTagName,
                AlarmType = MapAlarmType(SelectedOperator),
                Operator = SelectedOperator,
                Threshold = Threshold,
                Deadband = Deadband,
                ConditionExpression = condition,
                Severity = Severity,
                Title = Title,
                MessageTemplate = string.IsNullOrWhiteSpace(MessageTemplate)
                    ? $"{SelectedTagName?.Split('/').LastOrDefault() ?? AlarmCode} 当前值 {{Value}}，超过阈值 {Threshold}"
                    : MessageTemplate,
                Source = targetPath?.Value ?? SelectedResourcePath ?? string.Empty,
                IsEnabled = IsEnabled,
                AckPolicy = AckPolicy,
                ClearPolicy = AlarmClearPolicy.AutoClearWhenConditionFalse,
                DelayMs = DelayMs,
                Hysteresis = Hysteresis,
                CooldownSeconds = CooldownSeconds
            };

            await _repository.UpsertAsync(def);

            // 保存后重建规则工作流快照，确保运行时立即使用最新配置。
            await _ruleEngineService.ReloadAsync();

            PublishNotification("保存成功",
                $"规则 {def.RuleId} [{def.AlarmCode}] 已保存并自动生效。",
                NotificationType.Success);

            await LoadRulesAsync();
            SelectedRule = Rules.FirstOrDefault(r => r.RuleId == def.RuleId);
        }
        catch (Exception ex)
        {
            PublishNotification("保存失败", ex.Message, NotificationType.Error);
        }
    }

    private async void OnDeleteExecute()
    {
        if (IsEditMode && SelectedRule != null)
        {
            try
            {
                await _repository.DisableAsync(SelectedRule.RuleId);
                // 禁用规则后同步替换运行时快照，避免旧规则继续接收数据。
                await _ruleEngineService.ReloadAsync();
                PublishNotification("删除成功", $"规则 {SelectedRule.RuleId} 已禁用并热重载。", NotificationType.Success);
                await LoadRulesAsync();
                SelectedRule = null;
                IsEditMode = false;
            }
            catch (Exception ex)
            {
                PublishNotification("删除失败", ex.Message, NotificationType.Error);
            }
        }
    }

    private async void OnReloadEngineExecute()
    {
        try
        {
            // 手工重载也必须经过规则引擎，定义快照和已编译工作流保持一致。
            await _ruleEngineService.ReloadAsync();
            PublishNotification("热重载成功", "底层报警规则引擎已重新编译并生效。", NotificationType.Success);
        }
        catch (Exception ex)
        {
            PublishNotification("热重载失败", ex.Message, NotificationType.Error);
        }
    }

    private void LoadFromDefinition(AlarmDefinition def)
    {
        RuleId = def.RuleId;
        SelectedResourcePath = def.TargetResourcePath?.Value ?? def.ResourcePath?.Value;
        // 历史自定义规则可能没有 TagName，使用目标资源路径末段作为兼容值。
        SelectedTagName = !string.IsNullOrWhiteSpace(def.TagName)
            ? def.TagName
            : def.TargetResourcePath?.Value.Split('/').LastOrDefault();
        _resolvedTagId = def.TagId;
        SelectedOperator = def.Operator;
        Threshold = def.Threshold;
        Deadband = def.Deadband;
        AlarmCode = def.AlarmCode;
        Title = def.Title;
        Severity = def.Severity;
        DelayMs = def.DelayMs;
        Hysteresis = def.Hysteresis;
        CooldownSeconds = def.CooldownSeconds;
        AckPolicy = def.AckPolicy;
        MessageTemplate = def.MessageTemplate;
        IsEnabled = def.IsEnabled;

        UseCustomCondition = !string.IsNullOrWhiteSpace(def.ConditionExpression);
        ConditionExpression = def.ConditionExpression;

        UseCustomResourcePath = false;
        CustomResourcePath = def.ResourcePath?.Value;
        CustomTargetResourcePath = def.TargetResourcePath?.Value;
        CustomTagId = def.TagId;
    }

    private async Task LoadRulesAsync()
    {
        var rules = await _repository.LoadAllAsync();
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Rules.Clear();
            foreach (var r in rules)
                Rules.Add(r);
        });
    }

    private string GetOperatorDisplayText()
    {
        var opText = SelectedOperator switch
        {
            AlarmOperator.GreaterThan => "大于 (>)",
            AlarmOperator.GreaterThanOrEqual => "大于等于 (>=)",
            AlarmOperator.LessThan => "小于 (<)",
            AlarmOperator.LessThanOrEqual => "小于等于 (<=)",
            AlarmOperator.Equal => "等于 (==)",
            AlarmOperator.NotEqual => "不等于 (!=)",
            AlarmOperator.InRange => $"区间内 [{Threshold} ~ {Deadband}]",
            AlarmOperator.OutOfRange => $"区间外 (<{Threshold} 或 >{Deadband})",
            _ => SelectedOperator.ToString()
        };
        return $"{SelectedTagName?.Split('/').LastOrDefault() ?? "?"} {opText} {Threshold}";
    }

    private static AlarmType MapAlarmType(AlarmOperator op) => op switch
    {
        AlarmOperator.LessThan or AlarmOperator.LessThanOrEqual => AlarmType.Low,
        AlarmOperator.InRange or AlarmOperator.OutOfRange => AlarmType.High,
        _ => AlarmType.High
    };

    private void PublishNotification(string title, string message, NotificationType type)
    {
        _eventAggregator.GetEvent<NotificationEvent>().Publish(new NotificationMessage
        {
            Title = title,
            Message = message,
            Type = type
        });
    }
}
