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
        entity.InputTagNamesJson = rule.Inputs.Count > 0
            ? JsonSerializer.Serialize(rule.Inputs)
            : JsonSerializer.Serialize(rule.InputTagNames);
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

    private static CalculationRule ToDomain(CalculationRuleEntity entity)
    {
        var (bindings, legacyNames) = DeserializeInputs(entity.InputTagNamesJson);
        return new CalculationRule
        {
            RuleId = entity.RuleId,
            Expression = entity.Expression,
            Inputs = bindings,
            InputTagNames = legacyNames,
            TargetTagId = entity.TargetTagId,
            TargetTagName = entity.TargetTagName,
            TargetDataType = Enum.TryParse<TagDataType>(entity.TargetDataType, true, out var type) ? type : TagDataType.Float64,
            Enabled = entity.Enabled
        };
    }

    private static (List<CalculationInputBinding> Bindings, List<string> LegacyNames) DeserializeInputs(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0) return ([], []);
        return document.RootElement[0].ValueKind == JsonValueKind.Object
            ? (JsonSerializer.Deserialize<List<CalculationInputBinding>>(json) ?? [], [])
            : ([], JsonSerializer.Deserialize<List<string>>(json) ?? []);
    }

    private static void Validate(CalculationRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.RuleId) || string.IsNullOrWhiteSpace(rule.Expression) || rule.EffectiveInputs.Count == 0 || string.IsNullOrWhiteSpace(rule.TargetTagId))
            throw new InvalidOperationException("计算规则必须包含标识、表达式、输入数据点和目标数据点。");
        if (rule.EffectiveInputs.Any(input => string.IsNullOrWhiteSpace(input.Alias) || (string.IsNullOrWhiteSpace(input.TagId) && string.IsNullOrWhiteSpace(input.TagName))))
            throw new InvalidOperationException("计算规则输入必须包含表达式别名及数据点标识。");
        if (rule.EffectiveInputs.Any(input => string.Equals(input.TagId, rule.TargetTagId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("计算规则不能把输出数据点作为自身输入。");
    }
}
