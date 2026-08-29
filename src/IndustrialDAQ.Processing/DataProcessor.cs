// File: DataProcessor.cs  Module: Processing Engine  Author: IndustrialDAQ Team
using System.Collections.Concurrent;
using System.Data;
using System.Text.RegularExpressions;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndustrialDAQ.Processing;

/// <summary>
/// 数据加工引擎 — 订阅实时数据库变更流，对配置的计算规则进行表达式求值，
/// 并将计算结果写回实时数据库。作为 <see cref="IHostedService"/> 运行。
/// </summary>
public sealed class DataProcessor : IHostedService
{
    private readonly RealTimeStore _store;
    private readonly ILogger<DataProcessor> _logger;

    /// <summary>所有计算规则（线程安全列表）。</summary>
    private readonly ConcurrentDictionary<string, CalculationRule> _rules = new();

    /// <summary>当前输入值缓存 — Key=TagName, Value=double。</summary>
    private readonly ConcurrentDictionary<string, double> _currentValues = new();

    /// <summary>TagName -> 依赖此测点的 RuleId 集合（反向索引，加速查找）。</summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _tagToRules = new();

    private CancellationTokenSource? _cts;
    private Task? _consumeTask;

    /// <summary>
    /// 初始化数据加工引擎。
    /// </summary>
    public DataProcessor(RealTimeStore store, ILogger<DataProcessor> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 注册计算规则并建立反向索引。
    /// </summary>
    public void RegisterRule(CalculationRule rule)
    {
        _rules[rule.RuleId] = rule;

        foreach (string tagName in rule.InputTagNames)
        {
            _tagToRules.AddOrUpdate(tagName,
                _ => new HashSet<string> { rule.RuleId },
                (_, set) => { set.Add(rule.RuleId); return set; });
        }

        _logger.LogInformation("已注册计算规则: {RuleId} [{Expression}] -> {Target}",
            rule.RuleId, rule.Expression, rule.TargetTagName);
    }

    /// <summary>
    /// 批量注册计算规则。
    /// </summary>
    public void RegisterRules(IEnumerable<CalculationRule> rules)
    {
        foreach (var rule in rules) RegisterRule(rule);
    }

    /// <summary>使用持久化规则完整替换运行时快照，避免删除或停用后旧规则继续执行。</summary>
    public void ReplaceRules(IEnumerable<CalculationRule> rules)
    {
        _rules.Clear();
        _tagToRules.Clear();
        RegisterRules(rules);
        _logger.LogInformation("计算规则运行时快照已更新，共 {Count} 条", _rules.Count);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _consumeTask = Task.Run(() => ConsumeAsync(_cts.Token), _cts.Token);
        _logger.LogInformation("数据加工引擎已启动 ({Count} 条规则)", _rules.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("数据加工引擎正在停止...");
        _cts?.Cancel();

        if (_consumeTask is not null)
        {
            try { await _consumeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _logger.LogInformation("数据加工引擎已停止");
    }

    /// <summary>
    /// 主消费循环 — 监听实时数据变更，触发相关规则计算。
    /// </summary>
    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            var reader = _store.Subscribe();
            await foreach (TagValue value in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                // 跳过非数值类型（计算规则只处理数值）
                if (!TryConvertToDouble(value.Value, out double numericValue))
                    continue;

                // 更新当前值缓存
                _currentValues[value.TagName] = numericValue;

                // 查找依赖此测点的规则
                if (!_tagToRules.TryGetValue(value.TagName, out HashSet<string>? dependentRules))
                    continue;

                foreach (string ruleId in dependentRules)
                {
                    if (!_rules.TryGetValue(ruleId, out CalculationRule? rule) || !rule.Enabled)
                        continue;

                    EvaluateRule(rule);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常退出
        }
    }

    /// <summary>
    /// 求值单条计算规则，将结果写入实时数据库。
    /// </summary>
    private void EvaluateRule(CalculationRule rule)
    {
        try
        {
            // 构建表达式 — 将测点名替换为当前数值
            string expression = rule.Expression;
            bool allInputsReady = true;

            foreach (string tagName in rule.InputTagNames)
            {
                if (!_currentValues.TryGetValue(tagName, out double val))
                {
                    allInputsReady = false;
                    break;
                }
                // 替换变量名（使用 word boundary 避免部分匹配）
                expression = Regex.Replace(expression,
                    $@"\b{Regex.Escape(tagName)}\b",
                    val.ToString("R")); // "R" 格式保持浮点精度
            }

            if (!allInputsReady) return;

            // 使用 DataTable.Compute 安全求值
            object result = new DataTable().Compute(expression, null);

            double numericResult = Convert.ToDouble(result);

            // 如果结果为 NaN 或 Infinity，跳过
            if (double.IsNaN(numericResult) || double.IsInfinity(numericResult))
            {
                _logger.LogWarning("规则 {RuleId} 计算结果无效: {Result}", rule.RuleId, numericResult);
                return;
            }

            // 写入实时数据库
            var tagValue = new TagValue
            {
                TagId = rule.TargetTagId,
                TagName = rule.TargetTagName,
                Value = ConvertToTargetType(numericResult, rule.TargetDataType),
                Quality = Quality.Good,
                Timestamp = DateTimeOffset.UtcNow,
                DataType = MapToType(rule.TargetDataType)
            };

            _store.Update(tagValue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "规则 {RuleId} ({Expression}) 求值失败", rule.RuleId, rule.Expression);
        }
    }

    private static object ConvertToTargetType(double value, TagDataType dataType) => dataType switch
    {
        TagDataType.Int32 => (int)Math.Round(value),
        TagDataType.Int64 => (long)Math.Round(value),
        TagDataType.Float32 => (float)value,
        TagDataType.Float64 => value,
        TagDataType.Int16 => (short)Math.Round(value),
        TagDataType.UInt16 => (ushort)Math.Round(value),
        TagDataType.UInt32 => (uint)Math.Round(value),
        _ => value
    };

    private static Type MapToType(TagDataType dataType) => dataType switch
    {
        TagDataType.Int32 => typeof(int),
        TagDataType.Int64 => typeof(long),
        TagDataType.Float32 => typeof(float),
        TagDataType.Float64 => typeof(double),
        TagDataType.Int16 => typeof(short),
        TagDataType.UInt16 => typeof(ushort),
        TagDataType.UInt32 => typeof(uint),
        _ => typeof(double)
    };

    private static bool TryConvertToDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d: result = d; return true;
            case float f: result = f; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short s: result = s; return true;
            case byte b: result = b; return true;
            case ushort us: result = us; return true;
            case uint ui: result = ui; return true;
            default:
                result = 0;
                return false;
        }
    }
}
