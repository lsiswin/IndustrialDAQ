using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Core.ResourceTree;
using IndustrialDAQ.Infrastructure.Processing;
using IndustrialDAQ.Processing;
using IndustrialDAQ.UI.Services;
using Prism.Commands;
using Prism.Mvvm;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>傻瓜式计算规则向导：选择数据点和运算方式，系统自动生成表达式与虚拟数据点。</summary>
public sealed class CalculationRuleManagementViewModel : BindableBase
{
    private readonly CalculationRuleRepository _repository;
    private readonly DataProcessor _processor;
    private readonly IResourceTreeService _resourceTree;
    private readonly CalculationResourceSynchronizer _resourceSynchronizer;
    private readonly IAuthManager _authManager;
    private readonly SecurityAuditService _audit;
    private CalculationRuleListItem? _selectedRule;
    private CalculationTagOption? _inputA;
    private CalculationTagOption? _inputB;
    private CalculationOperationOption _operation;
    private string _editingRuleId = string.Empty;
    private string _outputName = string.Empty;
    private double _factor = 1;
    private bool _enabled = true;
    private string _statusText = "点击“新增计算”开始四步配置";

    public ObservableCollection<CalculationRuleListItem> Rules { get; } = [];
    public ObservableCollection<CalculationTagOption> AvailableInputs { get; } = [];
    public IReadOnlyList<CalculationOperationOption> Operations { get; } = CalculationOperationOption.All;
    public bool CanModify => _authManager.CanModify;
    public bool RequiresInputB => Operation.RequiresInputB;
    public bool RequiresFactor => Operation.RequiresFactor;
    public string PreviewExpression => BuildExpression();
    public string PreviewSummary => string.IsNullOrWhiteSpace(OutputName)
        ? "请填写输出名称"
        : $"{InputA?.DisplayText ?? "未选择"} → {Operation.Name} → Calculated/{EditingRuleId}\n输出：{OutputName}";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string EditingRuleId { get => _editingRuleId; private set { SetProperty(ref _editingRuleId, value); RaisePropertyChanged(nameof(PreviewSummary)); } }
    public string OutputName { get => _outputName; set { SetProperty(ref _outputName, value); RaisePreview(); SaveCommand.RaiseCanExecuteChanged(); } }
    public double Factor { get => _factor; set { SetProperty(ref _factor, value); RaisePreview(); SaveCommand.RaiseCanExecuteChanged(); } }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

    public CalculationRuleListItem? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (SetProperty(ref _selectedRule, value) && value is not null) LoadEditor(value.Rule);
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    public CalculationTagOption? InputA
    {
        get => _inputA;
        set { SetProperty(ref _inputA, value); RaisePreview(); SaveCommand.RaiseCanExecuteChanged(); }
    }

    public CalculationTagOption? InputB
    {
        get => _inputB;
        set { SetProperty(ref _inputB, value); RaisePreview(); SaveCommand.RaiseCanExecuteChanged(); }
    }

    public CalculationOperationOption Operation
    {
        get => _operation;
        set
        {
            if (!SetProperty(ref _operation, value)) return;
            RaisePropertyChanged(nameof(RequiresInputB));
            RaisePropertyChanged(nameof(RequiresFactor));
            RaisePreview();
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public DelegateCommand AddCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand DeleteCommand { get; }
    public DelegateCommand RefreshCommand { get; }

    public CalculationRuleManagementViewModel(
        CalculationRuleRepository repository,
        DataProcessor processor,
        IResourceTreeService resourceTree,
        CalculationResourceSynchronizer resourceSynchronizer,
        IAuthManager authManager,
        SecurityAuditService audit)
    {
        _repository = repository;
        _processor = processor;
        _resourceTree = resourceTree;
        _resourceSynchronizer = resourceSynchronizer;
        _authManager = authManager;
        _audit = audit;
        _operation = Operations[0];
        AddCommand = new DelegateCommand(StartNew, () => CanModify);
        SaveCommand = new DelegateCommand(SaveAsync, () => CanModify && CanSave());
        DeleteCommand = new DelegateCommand(DeleteAsync, () => CanModify && SelectedRule is not null);
        RefreshCommand = new DelegateCommand(RefreshAsync);
        _authManager.CurrentUserChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(CanModify));
            AddCommand.RaiseCanExecuteChanged(); SaveCommand.RaiseCanExecuteChanged(); DeleteCommand.RaiseCanExecuteChanged();
        };
        _ = LoadAsync();
    }

    private void StartNew()
    {
        SelectedRule = null;
        EditingRuleId = "calc-" + Guid.NewGuid().ToString("N")[..8];
        InputA = null; InputB = null; Operation = Operations[0]; Factor = 1; OutputName = "新计算量"; Enabled = true;
        StatusText = "按顺序完成：输入 A → 运算 → 输出名称 → 保存";
    }

    private async void SaveAsync()
    {
        try
        {
            var rule = BuildRule();
            await _repository.UpsertAsync(rule);
            var rules = await ReloadRuntimeAsync();
            await _audit.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username, "CalculationRuleSaved", $"Calculated/{rule.RuleId}", $"Expression={rule.Expression};Target={rule.TargetTagId}", true);
            LoadRulesIntoUi(rules, rule.RuleId);
            StatusText = $"✓ {rule.TargetTagName} 已保存，输入变化时自动计算";
        }
        catch (Exception ex) { StatusText = "✗ 保存失败：" + ex.Message; }
    }

    private async void DeleteAsync()
    {
        if (SelectedRule is null) return;
        var rule = SelectedRule.Rule;
        await _repository.DeleteAsync(rule.RuleId);
        var rules = await ReloadRuntimeAsync();
        await _audit.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username, "CalculationRuleDeleted", $"Calculated/{rule.RuleId}", rule.TargetTagName, true);
        LoadRulesIntoUi(rules);
        StartNew();
        StatusText = "规则和虚拟数据点已删除";
    }

    private async void RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await _resourceTree.ReloadAsync();
        LoadAvailableInputs();
        var rules = await _repository.LoadAsync();
        _processor.ReplaceRules(rules);
        await _resourceSynchronizer.SyncAsync(rules);
        LoadRulesIntoUi(rules);
        if (Rules.Count == 0) StartNew();
        StatusText = Rules.Count == 0 ? "暂无计算规则，请按四步新增" : $"已加载 {Rules.Count} 条计算规则";
    }

    private async Task<IReadOnlyList<CalculationRule>> ReloadRuntimeAsync()
    {
        var rules = await _repository.LoadAsync();
        _processor.ReplaceRules(rules);
        await _resourceSynchronizer.SyncAsync(rules);
        LoadAvailableInputs();
        return rules;
    }

    private void LoadRulesIntoUi(IReadOnlyList<CalculationRule> rules, string? selectedRuleId = null)
    {
        Rules.Clear();
        foreach (var rule in rules) Rules.Add(new CalculationRuleListItem(rule));
        SelectedRule = Rules.FirstOrDefault(item => item.Rule.RuleId == selectedRuleId) ?? Rules.FirstOrDefault();
    }

    private void LoadAvailableInputs()
    {
        AvailableInputs.Clear();
        foreach (var node in _resourceTree.Current.Nodes.Where(node => node.ResourceType == ResourceType.Tag && node.IsEnabled))
        {
            if (!TryReadTagMetadata(node, out var tagId, out var dataType) || !IsNumeric(dataType)) continue;
            AvailableInputs.Add(new CalculationTagOption(node.Path.Value, tagId, node.DisplayName, dataType));
        }
    }

    private void LoadEditor(CalculationRule rule)
    {
        EditingRuleId = rule.RuleId;
        OutputName = rule.TargetTagName;
        Enabled = rule.Enabled;
        var inputs = rule.EffectiveInputs;
        InputA = FindOption(inputs.ElementAtOrDefault(0));
        InputB = FindOption(inputs.ElementAtOrDefault(1));
        Operation = CalculationOperationOption.Infer(rule.Expression, inputs.Count, out var factor);
        Factor = factor;
    }

    private CalculationTagOption? FindOption(CalculationInputBinding? input) => input is null ? null :
        AvailableInputs.FirstOrDefault(option => option.TagId == input.TagId) ??
        AvailableInputs.FirstOrDefault(option => option.DisplayName == input.TagName);

    private CalculationRule BuildRule()
    {
        if (!CanSave()) throw new InvalidOperationException("请完成输入、运算方式和输出名称。");
        var inputs = new List<CalculationInputBinding> { InputA!.ToBinding("A") };
        if (RequiresInputB) inputs.Add(InputB!.ToBinding("B"));
        return new CalculationRule
        {
            RuleId = EditingRuleId,
            Inputs = inputs,
            Expression = BuildExpression(),
            TargetTagId = "virtual-" + EditingRuleId,
            TargetTagName = OutputName.Trim(),
            TargetDataType = TagDataType.Float64,
            Enabled = Enabled
        };
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(EditingRuleId) && InputA is not null &&
        (!RequiresInputB || InputB is not null) && (!RequiresFactor || Math.Abs(Factor) > double.Epsilon) &&
        !string.IsNullOrWhiteSpace(OutputName);

    private string BuildExpression() => Operation.BuildExpression(Factor);

    private void RaisePreview()
    {
        RaisePropertyChanged(nameof(PreviewExpression));
        RaisePropertyChanged(nameof(PreviewSummary));
    }

    private static bool TryReadTagMetadata(ResourceNode node, out string tagId, out TagDataType dataType)
    {
        tagId = string.Empty; dataType = TagDataType.String;
        try
        {
            using var document = JsonDocument.Parse(node.MetadataJson ?? "{}");
            tagId = document.RootElement.TryGetProperty("tagId", out var id) ? id.GetString() ?? string.Empty : node.Id;
            return document.RootElement.TryGetProperty("dataType", out var type) && Enum.TryParse(type.GetString(), true, out dataType);
        }
        catch { return false; }
    }

    private static bool IsNumeric(TagDataType type) => type is TagDataType.Int16 or TagDataType.Int32 or TagDataType.Int64 or TagDataType.UInt16 or TagDataType.UInt32 or TagDataType.Float32 or TagDataType.Float64;
}

public sealed record CalculationTagOption(string ResourcePath, string TagId, string DisplayName, TagDataType DataType)
{
    public string DisplayText => $"{ResourcePath} · {DisplayName}";
    public CalculationInputBinding ToBinding(string alias) => new() { Alias = alias, TagId = TagId, TagName = DisplayName, ResourcePath = ResourcePath };
}

public sealed record CalculationOperationOption(string Key, string Name, bool RequiresInputB, bool RequiresFactor)
{
    public static IReadOnlyList<CalculationOperationOption> All { get; } =
    [
        new("copy", "直接取值 A", false, false), new("add", "A + B 相加", true, false),
        new("subtract", "A - B 相减", true, false), new("average", "A、B 平均值", true, false),
        new("multiply", "A × 系数", false, true), new("divide", "A ÷ 系数", false, true),
        new("percent", "A 占 B 的百分比", true, false)
    ];

    public string BuildExpression(double factor) => Key switch
    {
        "add" => "A + B", "subtract" => "A - B", "average" => "(A + B) / 2",
        "multiply" => $"A * {factor.ToString("R", CultureInfo.InvariantCulture)}",
        "divide" => $"A / {factor.ToString("R", CultureInfo.InvariantCulture)}",
        "percent" => "(A / B) * 100", _ => "A"
    };

    public static CalculationOperationOption Infer(string expression, int inputCount, out double factor)
    {
        factor = 1;
        var normalized = expression.Replace(" ", string.Empty);
        if (normalized == "A+B") return All[1];
        if (normalized == "A-B") return All[2];
        if (normalized == "(A+B)/2") return All[3];
        if (normalized == "(A/B)*100") return All[6];
        if (normalized.StartsWith("A*", StringComparison.Ordinal) && double.TryParse(normalized[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out factor)) return All[4];
        if (normalized.StartsWith("A/", StringComparison.Ordinal) && double.TryParse(normalized[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out factor)) return All[5];
        return inputCount > 1 ? All[1] : All[0];
    }
}

public sealed class CalculationRuleListItem
{
    public CalculationRule Rule { get; }
    public string Name => Rule.TargetTagName;
    public string Inputs => string.Join("、", Rule.EffectiveInputs.Select(input => input.TagName));
    public string Formula => Rule.Expression;
    public bool Enabled => Rule.Enabled;
    public CalculationRuleListItem(CalculationRule rule) => Rule = rule;
}
