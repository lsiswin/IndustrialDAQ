using IndustrialDAQ.Alarm.RuleEngine;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Core.ResourceTree;
using Microsoft.Extensions.Logging;

namespace IndustrialDAQ.UI.Services;

/// <summary>依据设备数据点的模板关联首次生成数据库报警规则，运行时不再注册隐藏规则。</summary>
public sealed class AlarmRuleProvisioningService
{
    private readonly IAlarmDefinitionRepository _alarmRepository;
    private readonly IResourceTreeService _resourceTree;
    private readonly IRuleEngineService _ruleEngine;
    private readonly ILogger<AlarmRuleProvisioningService> _logger;

    public AlarmRuleProvisioningService(
        IAlarmDefinitionRepository alarmRepository,
        IResourceTreeService resourceTree,
        IRuleEngineService ruleEngine,
        ILogger<AlarmRuleProvisioningService> logger) =>
        (_alarmRepository, _resourceTree, _ruleEngine, _logger) =
        (alarmRepository, resourceTree, ruleEngine, logger);

    public async Task<int> ProvisionAsync(IEnumerable<DeviceConfig> devices, CancellationToken cancellationToken = default)
    {
        // 使用当前设备模板定义生成默认规则，避免旧数据库中的通用模板参数覆盖产线专用阈值。
        var dataPointTemplates = DeviceTemplateFactory.All.Values
            .SelectMany(device => device.DataPoints)
            .Where(point => point.AlarmTemplate is not null)
            .GroupBy(point => point.TemplateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var snapshot = _resourceTree.Current;
        var inserted = 0;
        var updated = 0;

        foreach (var device in devices)
        {
            foreach (var tag in device.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag.TemplateId)))
            {
                if (!dataPointTemplates.TryGetValue(tag.TemplateId!, out var pointTemplate) || pointTemplate.AlarmTemplate is null)
                    continue;

                var targetNode = snapshot.Nodes.FirstOrDefault(node => node.Id == tag.Id && node.ResourceType == ResourceType.Tag);
                if (targetNode is null) continue;

                var prefix = NormalizeIdentifier($"{device.Id}-{tag.Id}");
                foreach (var definition in pointTemplate.AlarmTemplate.CreateRules(tag.Id, tag.Name, device.Name, targetNode.Path, prefix))
                {
                    var existing = await _alarmRepository.FindByRuleIdAsync(definition.RuleId, cancellationToken);
                    // 页面保存会将来源改为 user；只有模板托管或旧版 template-* 规则允许自动修正。
                    if (existing is not null && !IsTemplateManaged(existing)) continue;
                    await _alarmRepository.UpsertAsync(definition, cancellationToken);
                    if (existing is null) inserted++; else updated++;
                }
            }
        }

        await _ruleEngine.ReloadAsync(cancellationToken);
        _logger.LogInformation("报警模板同步完成，新增 {Inserted} 条，更新 {Updated} 条，当前运行 {RuntimeCount} 条", inserted, updated, _ruleEngine.Current.Workflows.Count);
        return inserted;
    }

    private static bool IsTemplateManaged(AlarmDefinition definition) =>
        definition.MetadataJson?.Contains("\"origin\":\"template\"", StringComparison.OrdinalIgnoreCase) == true ||
        (definition.MetadataJson is null && definition.RuleId.StartsWith("template-", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeIdentifier(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());
}
