using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndustrialDAQ.Infrastructure.Entities;

/// <summary>计算规则持久化实体。</summary>
[Table("calculation_rules")]
public sealed class CalculationRuleEntity
{
    [Key] public string RuleId { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    public string InputTagNamesJson { get; set; } = "[]";
    public string TargetTagId { get; set; } = string.Empty;
    public string TargetTagName { get; set; } = string.Empty;
    public string TargetDataType { get; set; } = "Float64";
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
