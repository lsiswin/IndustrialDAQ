using System.Collections.ObjectModel;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Infrastructure.Processing;
using IndustrialDAQ.Processing;
using IndustrialDAQ.UI.Services;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>计算规则配置入口，持久化后立即刷新数据加工引擎。</summary>
public sealed class CalculationRuleManagementViewModel : BindableBase
{
    private readonly CalculationRuleRepository _repository;
    private readonly DataProcessor _processor;
    private readonly IAuthManager _authManager;
    private CalculationRuleItem? _selectedRule;
    private string _statusText = "计算规则由实时数据变化自动触发";

    public ObservableCollection<CalculationRuleItem> Rules { get; } = [];
    public IReadOnlyList<TagDataType> NumericTypes { get; } = [TagDataType.Int16, TagDataType.Int32, TagDataType.Int64, TagDataType.UInt16, TagDataType.UInt32, TagDataType.Float32, TagDataType.Float64];
    public CalculationRuleItem? SelectedRule { get => _selectedRule; set { SetProperty(ref _selectedRule, value); DeleteCommand.RaiseCanExecuteChanged(); } }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public bool CanModify => _authManager.CanModify;
    public DelegateCommand AddCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand DeleteCommand { get; }
    public DelegateCommand RefreshCommand { get; }

    public CalculationRuleManagementViewModel(CalculationRuleRepository repository, DataProcessor processor, IAuthManager authManager)
    {
        _repository = repository;
        _processor = processor;
        _authManager = authManager;
        AddCommand = new DelegateCommand(Add, () => CanModify);
        SaveCommand = new DelegateCommand(SaveAsync, () => CanModify);
        DeleteCommand = new DelegateCommand(DeleteAsync, () => CanModify && SelectedRule is not null);
        RefreshCommand = new DelegateCommand(RefreshAsync);
        _authManager.CurrentUserChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(CanModify));
            AddCommand.RaiseCanExecuteChanged(); SaveCommand.RaiseCanExecuteChanged(); DeleteCommand.RaiseCanExecuteChanged();
        };
        _ = LoadAsync();
    }

    private void Add()
    {
        var item = new CalculationRuleItem { RuleId = "calc-" + Guid.NewGuid().ToString("N")[..8], Enabled = true };
        Rules.Add(item);
        SelectedRule = item;
    }

    private async void SaveAsync()
    {
        try
        {
            foreach (var item in Rules) await _repository.UpsertAsync(item.ToDomain());
            await ReloadRuntimeAsync();
            StatusText = $"已保存并热加载 {Rules.Count} 条计算规则";
        }
        catch (Exception ex) { StatusText = "保存失败：" + ex.Message; }
    }

    private async void DeleteAsync()
    {
        if (SelectedRule is null) return;
        await _repository.DeleteAsync(SelectedRule.RuleId);
        Rules.Remove(SelectedRule);
        SelectedRule = null;
        await ReloadRuntimeAsync();
        StatusText = "规则已删除并从运行时移除";
    }

    private async void RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        var rules = await _repository.LoadAsync();
        Rules.Clear();
        foreach (var rule in rules) Rules.Add(CalculationRuleItem.FromDomain(rule));
        _processor.ReplaceRules(rules);
        StatusText = $"已加载 {Rules.Count} 条计算规则";
    }

    private async Task ReloadRuntimeAsync() => _processor.ReplaceRules(await _repository.LoadAsync());
}

public sealed class CalculationRuleItem : BindableBase
{
    private string _ruleId = string.Empty, _expression = string.Empty, _inputTags = string.Empty, _targetTagId = string.Empty, _targetTagName = string.Empty;
    private TagDataType _targetDataType = TagDataType.Float64;
    private bool _enabled;
    public string RuleId { get => _ruleId; set => SetProperty(ref _ruleId, value); }
    public string Expression { get => _expression; set => SetProperty(ref _expression, value); }
    public string InputTags { get => _inputTags; set => SetProperty(ref _inputTags, value); }
    public string TargetTagId { get => _targetTagId; set => SetProperty(ref _targetTagId, value); }
    public string TargetTagName { get => _targetTagName; set => SetProperty(ref _targetTagName, value); }
    public TagDataType TargetDataType { get => _targetDataType; set => SetProperty(ref _targetDataType, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

    public CalculationRule ToDomain() => new()
    {
        RuleId = RuleId.Trim(), Expression = Expression.Trim(),
        InputTagNames = InputTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        TargetTagId = TargetTagId.Trim(), TargetTagName = string.IsNullOrWhiteSpace(TargetTagName) ? TargetTagId.Trim() : TargetTagName.Trim(),
        TargetDataType = TargetDataType, Enabled = Enabled
    };

    public static CalculationRuleItem FromDomain(CalculationRule rule) => new()
    {
        RuleId = rule.RuleId, Expression = rule.Expression, InputTags = string.Join(", ", rule.InputTagNames), TargetTagId = rule.TargetTagId,
        TargetTagName = rule.TargetTagName, TargetDataType = rule.TargetDataType, Enabled = rule.Enabled
    };
}
