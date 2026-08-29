// File: DaqDbContext.cs  Module: Infrastructure  Author: IndustrialDAQ Team
using IndustrialDAQ.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace IndustrialDAQ.Infrastructure;

/// <summary>
/// 工业数据采集数据库上下文 — SQLite 单机版。
/// 使用连接池和 WAL 模式以支持并发读写。
/// </summary>
public sealed class DaqDbContext : DbContext
{
    /// <summary>历史记录表。</summary>
    public DbSet<HistoricalRecord> HistoricalRecords => Set<HistoricalRecord>();

    /// <summary>报警历史表。</summary>
    public DbSet<AlarmHistoryEntity> AlarmHistories => Set<AlarmHistoryEntity>();

    /// <summary>设备模板表。</summary>
    public DbSet<DeviceTemplateEntity> DeviceTemplates => Set<DeviceTemplateEntity>();

    /// <summary>数据点模板表。</summary>
    public DbSet<DataPointTemplateEntity> DataPointTemplates => Set<DataPointTemplateEntity>();

    /// <summary>报警模板表。</summary>
    public DbSet<AlarmTemplateEntity> AlarmTemplates => Set<AlarmTemplateEntity>();

    /// <summary>Runtime alarm definition configuration table.</summary>
    public DbSet<AlarmDefinitionEntity> AlarmDefinitions => Set<AlarmDefinitionEntity>();

    /// <summary>趋势模板表。</summary>
    public DbSet<TrendTemplateEntity> TrendTemplates => Set<TrendTemplateEntity>();

    /// <summary>运行时资源树表。</summary>
    public DbSet<ResourceNodeEntity> ResourceNodes => Set<ResourceNodeEntity>();

    /// <summary>Runtime permission policy table.</summary>
    public DbSet<PermissionPolicyEntity> PermissionPolicies => Set<PermissionPolicyEntity>();

    /// <summary>User account table.</summary>
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<RoleEntity> Roles => Set<RoleEntity>();
    public DbSet<UserRoleEntity> UserRoles => Set<UserRoleEntity>();
    public DbSet<SecurityAuditEntity> SecurityAudits => Set<SecurityAuditEntity>();
    public DbSet<CalculationRuleEntity> CalculationRules => Set<CalculationRuleEntity>();

    /// <summary>
    /// 使用选项配置（由 DI 注入连接字符串）。
    /// </summary>
    public DaqDbContext(DbContextOptions<DaqDbContext> options)
        : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HistoricalRecord>(entity =>
        {
            entity.HasIndex(e => e.TagId);
            entity.HasIndex(e => e.Timestamp);
            // 复合索引：按测点 + 时间范围查询是最高频场景
            entity.HasIndex(e => new { e.TagId, e.Timestamp });
        });

        modelBuilder.Entity<AlarmHistoryEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AlarmId);
            entity.HasIndex(e => e.RuleId);
            entity.HasIndex(e => e.AlarmCode);
            entity.HasIndex(e => e.TagId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Severity);
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => e.OccurrenceId);
            // 复合索引：按状态 + 时间查询是最高频场景
            entity.HasIndex(e => new { e.Status, e.OccurredAt });
            // 复合索引：按规则 + 发生标识查询
            entity.HasIndex(e => new { e.RuleId, e.OccurrenceId });
        });

        modelBuilder.Entity<DeviceTemplateEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TemplateId).IsUnique();
            entity.HasIndex(e => e.IsBuiltIn);
        });

        modelBuilder.Entity<DataPointTemplateEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DeviceTemplateId);
            entity.HasIndex(e => e.TemplateId);
            entity.HasOne<DeviceTemplateEntity>()
                .WithMany()
                .HasForeignKey(e => e.DeviceTemplateId);
        });

        modelBuilder.Entity<AlarmTemplateEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TemplateId).IsUnique();
            entity.HasIndex(e => e.IsBuiltIn);
        });

        modelBuilder.Entity<AlarmDefinitionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RuleId).IsUnique();
            entity.HasIndex(e => e.AlarmCode);
            entity.HasIndex(e => e.ResourcePath);
            entity.HasIndex(e => e.TargetResourcePath);
            entity.HasIndex(e => e.TagId);
            entity.HasIndex(e => e.IsEnabled);
            entity.HasIndex(e => new { e.TargetResourcePath, e.AlarmCode });
        });

        modelBuilder.Entity<TrendTemplateEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TemplateId).IsUnique();
            entity.HasIndex(e => e.IsBuiltIn);
        });

        modelBuilder.Entity<ResourceNodeEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ResourcePath).IsUnique();
            entity.HasIndex(e => e.ParentId);
            entity.HasIndex(e => e.ResourceType);
            entity.HasIndex(e => e.IsEnabled);
            entity.HasIndex(e => new { e.ParentId, e.SortOrder });
        });

        modelBuilder.Entity<PermissionPolicyEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SubjectId);
            entity.HasIndex(e => e.SubjectType);
            entity.HasIndex(e => e.ResourcePath);
            entity.HasIndex(e => e.Action);
            entity.HasIndex(e => e.IsEnabled);
            entity.HasIndex(e => new { e.SubjectType, e.SubjectId, e.Action });
            entity.HasIndex(e => new { e.ResourcePath, e.Action });
        });

        modelBuilder.Entity<UserEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Username).IsUnique();
            entity.HasIndex(e => e.IsActive);
        });
        modelBuilder.Entity<RoleEntity>(entity => { entity.HasKey(item => item.Id); entity.HasIndex(item => item.Name).IsUnique(); });
        modelBuilder.Entity<UserRoleEntity>(entity =>
        {
            entity.HasKey(item => new { item.UserId, item.RoleId });
            entity.HasIndex(item => item.RoleId);
        });
        modelBuilder.Entity<SecurityAuditEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.OccurredAtUtc);
            entity.HasIndex(item => item.UserId);
            entity.HasIndex(item => item.Action);
        });
        modelBuilder.Entity<CalculationRuleEntity>(entity =>
        {
            entity.HasKey(item => item.RuleId);
            entity.HasIndex(item => item.Enabled);
            entity.HasIndex(item => item.TargetTagId);
        });
    }
}
