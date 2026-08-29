// File: HistoricalRecord.cs  Module: Infrastructure (Entities)  Author: IndustrialDAQ Team
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndustrialDAQ.Infrastructure.Entities;

/// <summary>
/// 历史记录实体 — 对应 SQLite 中的 historical_records 表。
/// 存储经过死区压缩后的测点值，用于历史趋势查询和报表。
/// </summary>
[Table("historical_records")]
public sealed class HistoricalRecord
{
    /// <summary>自增主键。</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>测点唯一标识。</summary>
    [Required]
    [MaxLength(64)]
    public string TagId { get; set; } = string.Empty;

    /// <summary>测点名称（冗余存储以加速查询）。</summary>
    [Required]
    [MaxLength(128)]
    public string TagName { get; set; } = string.Empty;

    /// <summary>
    /// 测点值（序列化为字符串存储）。
    /// 布尔 → "True"/"False"，数值 → 字符串表示，null → 空串。
    /// </summary>
    [MaxLength(256)]
    public string? Value { get; set; }

    /// <summary>原始 CLR 类型名称，用于从历史库恢复 bool、string 和各类数值。</summary>
    [MaxLength(128)]
    public string ValueType { get; set; } = "System.String";

    /// <summary>质量码（存储枚举底层字节值）。</summary>
    public byte QualityCode { get; set; }

    /// <summary>
    /// 采集时间戳，ISO 8601 格式存储（SQLite 不原生支持 DateTimeOffset）。
    /// 示例: "2026-05-04T08:43:09.0270000+00:00"
    /// </summary>
    [Required]
    [MaxLength(48)]
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>创建该记录的设备名称。</summary>
    [MaxLength(128)]
    public string? DeviceName { get; set; }
}
