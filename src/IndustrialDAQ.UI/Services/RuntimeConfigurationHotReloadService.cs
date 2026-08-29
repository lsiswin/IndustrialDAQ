using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using IndustrialDAQ.Alarm.RuleEngine;
using IndustrialDAQ.Core.Authorization;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Core.ResourceTree;
using IndustrialDAQ.Infrastructure.Processing;
using IndustrialDAQ.Processing;

namespace IndustrialDAQ.UI.Services;

/// <summary>报警、权限和计算规则 JSON 文件的统一热更新入口。</summary>
public sealed class RuntimeConfigurationHotReloadService
{
    private readonly IAlarmDefinitionRepository _alarmRepository;
    private readonly IRuleEngineService _ruleEngine;
    private readonly IAuthorizationRepository _authorizationRepository;
    private readonly IAuthorizationService _authorizationService;
    private readonly CalculationRuleRepository _calculationRepository;
    private readonly DataProcessor _processor;
    private readonly SecurityAuditService _audit;
    private readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter(), new ResourcePathJsonConverter() } };

    public RuntimeConfigurationHotReloadService(IAlarmDefinitionRepository alarmRepository, IRuleEngineService ruleEngine, IAuthorizationRepository authorizationRepository, IAuthorizationService authorizationService, CalculationRuleRepository calculationRepository, DataProcessor processor, SecurityAuditService audit) =>
        (_alarmRepository, _ruleEngine, _authorizationRepository, _authorizationService, _calculationRepository, _processor, _audit) = (alarmRepository, ruleEngine, authorizationRepository, authorizationService, calculationRepository, processor, audit);

    public static bool IsRuntimeConfiguration(string path) => Path.GetFileName(path).ToLowerInvariant() is "alarm-definitions.json" or "permission-policies.json" or "calculation-rules.json";

    public async Task ReloadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(path).ToLowerInvariant();
        var json = await ReadStableFileAsync(path, cancellationToken);
        int count;
        switch (fileName)
        {
            case "alarm-definitions.json":
                var alarms = JsonSerializer.Deserialize<List<AlarmDefinition>>(json, _options) ?? [];
                foreach (var alarm in alarms) await _alarmRepository.UpsertAsync(alarm, cancellationToken);
                await _ruleEngine.ReloadAsync(cancellationToken);
                count = alarms.Count;
                break;
            case "permission-policies.json":
                var policies = JsonSerializer.Deserialize<List<PermissionPolicyDto>>(json, _options) ?? [];
                foreach (var policy in policies) await _authorizationRepository.UpsertPolicyAsync(policy.ToDomain(), cancellationToken);
                await _authorizationService.ReloadAsync(cancellationToken);
                count = policies.Count;
                break;
            case "calculation-rules.json":
                var calculations = JsonSerializer.Deserialize<List<CalculationRule>>(json, _options) ?? [];
                foreach (var rule in calculations) await _calculationRepository.UpsertAsync(rule, cancellationToken);
                _processor.ReplaceRules(await _calculationRepository.LoadAsync(cancellationToken));
                count = calculations.Count;
                break;
            default: return;
        }
        await _audit.RecordAsync("system", "System", "RuntimeConfigurationReloaded", "System/Configuration/" + fileName, $"Count={count}", true);
    }

    private static async Task<string> ReadStableFileAsync(string path, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { return await File.ReadAllTextAsync(path, cancellationToken); }
            catch (IOException ex) { lastError = ex; await Task.Delay(100 * (attempt + 1), cancellationToken); }
        }
        throw new IOException("配置文件持续被占用，无法热加载。", lastError);
    }

    private sealed class ResourcePathJsonConverter : JsonConverter<ResourcePath>
    {
        public override ResourcePath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? throw new JsonException("资源路径不能为空。"));
        public override void Write(Utf8JsonWriter writer, ResourcePath value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }

    private sealed class PermissionPolicyDto
    {
        public string? Id { get; set; }
        public PermissionSubjectType SubjectType { get; set; }
        public string SubjectId { get; set; } = string.Empty;
        public string ResourcePath { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public PermissionEffect Effect { get; set; }
        public bool Inherit { get; set; } = true;
        public int Priority { get; set; }
        public bool IsEnabled { get; set; } = true;
        public PermissionPolicy ToDomain() => new() { Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id, SubjectType = SubjectType, SubjectId = SubjectId, ResourcePath = new ResourcePath(ResourcePath), Action = Action, Effect = Effect, Inherit = Inherit, Priority = Priority, IsEnabled = IsEnabled, UpdatedAtUtc = DateTime.UtcNow };
    }
}
