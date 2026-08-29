using System.Text.Json;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace IndustrialDAQ.Infrastructure.Processing;

/// <summary>计算规则仓储，负责领域模型与 SQLite 实体转换。</summary>
public sealed class CalculationRuleRepository
{
    private readonly IDbContextFactory<DaqDbContext> _factory;
    public CalculationRuleRepository(IDbContextFactory<DaqDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<CalculationRule>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.CalculationRules.AsNoTracking().OrderBy(item => item.RuleId).ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToArray();
    }

    public async Task UpsertAsync(CalculationRule rule, CancellationToken cancellationToken = default)
    {
        Validate(rule);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.CalculationRules.FirstOrDefaultAsync(item => item.RuleId == rule.RuleId, cancellationToken);
        if (entity is null)
        {
            entity = new CalculationRuleEntity { RuleId = rule.RuleId };
            db.CalculationRules.Add(entity);
        }
        entity.Expression = rule.Expression;
        entity.InputTagNamesJson = JsonSerializer.Serialize(rule.InputTagNames);
        entity.TargetTagId = rule.TargetTagId;
        entity.TargetTagName = rule.TargetTagName;
        entity.TargetDataType = rule.TargetDataType.ToString();
        entity.Enabled = rule.Enabled;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string ruleId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await db.CalculationRules.Where(item => item.RuleId == ruleId).ExecuteDeleteAsync(cancellationToken);
    }

    private static CalculationRule ToDomain(CalculationRuleEntity entity) => new()
    {
        RuleId = entity.RuleId,
        Expression = entity.Expression,
        InputTagNames = JsonSerializer.Deserialize<List<string>>(entity.InputTagNamesJson) ?? [],
        TargetTagId = entity.TargetTagId,
        TargetTagName = entity.TargetTagName,
        TargetDataType = Enum.TryParse<TagDataType>(entity.TargetDataType, true, out var type) ? type : TagDataType.Float64,
        Enabled = entity.Enabled
    };

    private static void Validate(CalculationRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.RuleId) || string.IsNullOrWhiteSpace(rule.Expression) || rule.InputTagNames.Count == 0 || string.IsNullOrWhiteSpace(rule.TargetTagId))
            throw new InvalidOperationException("计算规则必须包含标识、表达式、输入数据点和目标数据点。");
    }
}
