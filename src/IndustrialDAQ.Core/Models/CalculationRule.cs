namespace IndustrialDAQ.Core.Models;

/// <summary>
/// 计算规则 — 定义一个表达式，将多个测点值计算后输出到目标测点。
/// 表达式中的变量名对应 <see cref="InputTagNames"/> 中的测点名称。
/// </summary>
public sealed class CalculationRule
{
    /// <summary>规则唯一标识。</summary>
    public string RuleId { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>数学表达式，如 "(Temp1 + Temp2) / 2" 或 "Flow * 1.2"。</summary>
    public string Expression { get; init; } = string.Empty;

    /// <summary>表达式输入变量对应的测点名称列表。</summary>
    public List<string> InputTagNames { get; init; } = new();

    /// <summary>结构化输入绑定；Alias 用于表达式，TagId 是运行时唯一匹配键。</summary>
    public List<CalculationInputBinding> Inputs { get; init; } = new();

    /// <summary>兼容旧版按 TagName 保存的规则。</summary>
    public IReadOnlyList<CalculationInputBinding> EffectiveInputs => Inputs.Count > 0
        ? Inputs
        : InputTagNames.Select(name => new CalculationInputBinding { Alias = name, TagName = name }).ToArray();

    /// <summary>计算结果写入的目标测点 ID。</summary>
    public string TargetTagId { get; init; } = string.Empty;

    /// <summary>目标测点名称。</summary>
    public string TargetTagName { get; init; } = string.Empty;

    /// <summary>目标测点数据类型。</summary>
    public TagDataType TargetDataType { get; init; } = TagDataType.Float64;

    /// <summary>是否启用此规则。</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>计算表达式变量与现场数据点之间的稳定绑定。</summary>
public sealed record CalculationInputBinding
{
    public string Alias { get; init; } = string.Empty;
    public string TagId { get; init; } = string.Empty;
    public string TagName { get; init; } = string.Empty;
    public string ResourcePath { get; init; } = string.Empty;
}
