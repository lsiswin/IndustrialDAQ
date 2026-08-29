using System.Text.Json;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Core.ResourceTree;

namespace IndustrialDAQ.UI.Services;

/// <summary>将计算规则输出镜像为资源树虚拟数据点，供报警、趋势和权限模块统一引用。</summary>
public sealed class CalculationResourceSynchronizer
{
    private static readonly ResourcePath RootPath = new("Calculated");
    private const string RootId = "$calculated-root$";
    private readonly IResourceTreeRepository _repository;
    private readonly IResourceTreeService _resourceTree;

    public CalculationResourceSynchronizer(IResourceTreeRepository repository, IResourceTreeService resourceTree) =>
        (_repository, _resourceTree) = (repository, resourceTree);

    public async Task SyncAsync(IEnumerable<CalculationRule> rules, CancellationToken cancellationToken = default)
    {
        // 此前的虚拟节点全部由本服务管理，按规则快照重建可清除已删除规则。
        await _repository.DeleteAsync(RootPath, cancellationToken);
        await _repository.UpsertAsync(new ResourceNode
        {
            Id = RootId, Path = RootPath, Name = "Calculated", DisplayName = "计算数据点",
            ResourceType = ResourceType.Area, IsEnabled = true
        }, cancellationToken);

        var order = 1;
        foreach (var rule in rules.Where(rule => rule.Enabled))
        {
            await _repository.UpsertAsync(new ResourceNode
            {
                Id = rule.TargetTagId,
                ParentId = RootId,
                Path = new ResourcePath($"Calculated/{NormalizePathSegment(rule.RuleId)}"),
                Name = rule.TargetTagName,
                DisplayName = rule.TargetTagName,
                ResourceType = ResourceType.Tag,
                SortOrder = order++,
                IsEnabled = true,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    tagId = rule.TargetTagId,
                    dataType = rule.TargetDataType.ToString(),
                    calculationRuleId = rule.RuleId,
                    isVirtual = true
                })
            }, cancellationToken);
        }

        await _resourceTree.ReloadAsync(cancellationToken);
    }

    private static string NormalizePathSegment(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unnamed" : value.Trim().Replace('/', '-').Replace('\\', '-');
}
