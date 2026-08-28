using System.ComponentModel.DataAnnotations.Schema;

namespace IndustrialDAQ.Infrastructure.Entities;

/// <summary>关键工业操作审计记录。</summary>
[Table("security_audits")]
public sealed class SecurityAuditEntity
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ResourcePath { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public bool Success { get; set; }
}
