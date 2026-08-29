// File: AlarmTemplate.cs  Module: Core (Models)  Author: IndustrialDAQ Team
using System.Text.Json;
using IndustrialDAQ.Core.ResourceTree;
namespace IndustrialDAQ.Core.Models;

/// <summary>
/// 报警模板 — 预定义的报警规则模板，适用于常见工业场景。
/// 通过 <see cref="CreateRules"/> 根据具体测点生成 <see cref="AlarmDefinition"/>。
/// </summary>
public sealed class AlarmTemplate
{
    /// <summary>模板唯一标识。</summary>
    public string TemplateId { get; init; } = string.Empty;

    /// <summary>模板名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>适用的数据类型。</summary>
    public TagDataType ApplicableDataType { get; init; } = TagDataType.Float32;

    /// <summary>工程单位（如 "°C", "bar", "rpm"）。</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>高限阈值（0 表示不启用）。</summary>
    public double HighThreshold { get; init; }

    /// <summary>高高限阈值（0 表示不启用）。</summary>
    public double HighHighThreshold { get; init; }

    /// <summary>低限阈值（0 表示不启用）。</summary>
    public double LowThreshold { get; init; }

    /// <summary>低低限阈值（0 表示不启用）。</summary>
    public double LowLowThreshold { get; init; }

    /// <summary>滞回值（死区）。</summary>
    public double Hysteresis { get; init; }

    /// <summary>默认报警级别。</summary>
    public AlarmSeverity Severity { get; init; } = AlarmSeverity.Warning;

    /// <summary>冷却时间（秒）。</summary>
    public int CooldownSeconds { get; init; } = 60;

    /// <summary>模板支持的报警类型集合。</summary>
    public AlarmType[] SupportedAlarmTypes { get; init; } = [];

    /// <summary>
    /// 根据具体测点信息生成报警规则。
    /// </summary>
    /// <param name="tagId">测点 ID。</param>
    /// <param name="tagName">测点名称。</param>
    /// <param name="source">来源设备名称。</param>
    /// <returns>报警规则列表（每个支持的报警类型生成一条规则）。</returns>
    public IReadOnlyList<AlarmDefinition> CreateRules(
        string tagId,
        string tagName,
        string source,
        ResourcePath? targetResourcePath = null,
        string? ruleIdPrefix = null)
    {
        var rules = new List<AlarmDefinition>();

        foreach (var alarmType in SupportedAlarmTypes)
        {
            double threshold = alarmType switch
            {
                AlarmType.High => HighThreshold,
                AlarmType.HighHigh => HighHighThreshold,
                AlarmType.Low => LowThreshold,
                AlarmType.LowLow => LowLowThreshold,
                _ => 0
            };

            // 跳过阈值为 0 的规则（表示不启用）
            if (threshold == 0 && alarmType != AlarmType.Bool)
                continue;

            AlarmSeverity severity = alarmType switch
            {
                AlarmType.HighHigh or AlarmType.LowLow => AlarmSeverity.Critical,
                _ => Severity
            };

            var alarmOperator = alarmType switch
            {
                AlarmType.High or AlarmType.HighHigh => AlarmOperator.GreaterThan,
                AlarmType.Low or AlarmType.LowLow => AlarmOperator.LessThan,
                AlarmType.Bool => AlarmOperator.Equal,
                _ => AlarmOperator.Equal
            };

            string title = $"{tagName} {alarmType} 报警";
            string messageTemplate = alarmType == AlarmType.Bool
                ? $"{tagName} 状态报警"
                : $"{tagName} {{Value}} {Unit} 超限 ({alarmType}: {threshold})";

            var alarmCode = $"{tagName.Replace('.', '_').Replace(' ', '_')}_{alarmType}".ToUpperInvariant();
            var stablePrefix = string.IsNullOrWhiteSpace(ruleIdPrefix) ? tagId : ruleIdPrefix;
            var ruleId = $"template-{stablePrefix}-{alarmType}".ToLowerInvariant();
            rules.Add(new AlarmDefinition
            {
                Id = ruleId,
                RuleId = ruleId,
                AlarmCode = alarmCode,
                TargetResourcePath = targetResourcePath,
                ResourcePath = targetResourcePath is null ? null : new ResourcePath($"{targetResourcePath.Value}/Alarm/{alarmCode}"),
                TagId = tagId,
                TagName = tagName,
                AlarmType = alarmType,
                Operator = alarmOperator,
                Threshold = alarmType == AlarmType.Bool ? 1 : threshold,
                ConditionExpression = alarmType == AlarmType.Bool ? "Value == true" : string.Empty,
                Hysteresis = Hysteresis,
                Severity = severity,
                Title = title,
                MessageTemplate = messageTemplate,
                Source = source,
                IsEnabled = true,
                CooldownSeconds = CooldownSeconds,
                MetadataJson = JsonSerializer.Serialize(new { origin = "template", templateId = TemplateId })
            });
        }

        return rules;
    }
}
