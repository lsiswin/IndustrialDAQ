using System.Text.Json;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Core.ResourceTree;
using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Runtime;

/// <summary>将相机、检测任务和结果 Tag 镜像到 ResourcePath 资源树。</summary>
public sealed class VisionResourceSynchronizer
{
    private static readonly ResourcePath RootPath = new("Vision");
    private const string RootId = "$vision-root$";
    private readonly IResourceTreeRepository _repository;
    private readonly IResourceTreeService _resourceTree;

    public VisionResourceSynchronizer(IResourceTreeRepository repository, IResourceTreeService resourceTree) =>
        (_repository, _resourceTree) = (repository, resourceTree);

    public async Task SyncAsync(
        IReadOnlyList<VisionCameraConfig> cameras,
        IReadOnlyList<VisionInspectionTask> tasks,
        CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(RootPath, cancellationToken);
        await _repository.UpsertAsync(Node(RootId, null, "Vision", "机器视觉", ResourceType.Factory, 0), cancellationToken);

        var order = 1;
        foreach (var camera in cameras.Where(item => item.IsEnabled))
        {
            var cameraId = $"vision-camera-{camera.CameraId}";
            var cameraPath = $"Vision/{Segment(camera.Name)}";
            await _repository.UpsertAsync(Node(cameraId, RootId, cameraPath, camera.Name, ResourceType.Device, order++, new { cameraId = camera.CameraId, driverType = camera.DriverType }), cancellationToken);

            var taskOrder = 1;
            foreach (var task in tasks.Where(item => item.IsEnabled && item.CameraId == camera.CameraId))
            {
                var taskNodeId = $"vision-task-{task.TaskId}";
                var taskPath = $"{cameraPath}/{Segment(task.Name)}";
                await _repository.UpsertAsync(Node(taskNodeId, cameraId, taskPath, task.Name, ResourceType.Cell, taskOrder++, new { taskId = task.TaskId, algorithmType = task.AlgorithmType }), cancellationToken);
                await AddGroupAsync(task, taskNodeId, taskPath, "Status", "运行状态", [
                    (VisionTagCatalog.Connected(task.TaskId), "Camera.Connected", TagDataType.Bool),
                    (VisionTagCatalog.Running(task.TaskId), "Task.Running", TagDataType.Bool)
                ], cancellationToken);
                await AddGroupAsync(task, taskNodeId, taskPath, "Result", "检测结果", [
                    (VisionTagCatalog.Pass(task.TaskId), "Result.Pass", TagDataType.Bool),
                    (VisionTagCatalog.CapPresent(task.TaskId), "Result.CapPresent", TagDataType.Bool),
                    (VisionTagCatalog.MatchScore(task.TaskId), "Result.MatchScore", TagDataType.Float64),
                    (VisionTagCatalog.ProcessingTime(task.TaskId), "Result.ProcessingTime", TagDataType.Float64)
                ], cancellationToken);
                await AddGroupAsync(task, taskNodeId, taskPath, "Statistics", "检测统计", [
                    (VisionTagCatalog.TotalCount(task.TaskId), "Statistics.TotalCount", TagDataType.Int64),
                    (VisionTagCatalog.NgCount(task.TaskId), "Statistics.NgCount", TagDataType.Int64),
                    (VisionTagCatalog.PassRate(task.TaskId), "Statistics.PassRate", TagDataType.Float64)
                ], cancellationToken);
            }
        }
        await _resourceTree.ReloadAsync(cancellationToken);
    }

    private async Task AddGroupAsync(
        VisionInspectionTask task, string taskNodeId, string taskPath, string groupName, string displayName,
        IReadOnlyList<(string Id, string Name, TagDataType Type)> tags, CancellationToken cancellationToken)
    {
        var groupId = $"vision-{task.TaskId}-{groupName.ToLowerInvariant()}";
        var groupPath = $"{taskPath}/{groupName}";
        await _repository.UpsertAsync(Node(groupId, taskNodeId, groupPath, displayName, ResourceType.Area, 0), cancellationToken);
        var order = 1;
        foreach (var tag in tags)
        {
            await _repository.UpsertAsync(Node(tag.Id, groupId, $"{groupPath}/{tag.Name.Split('.').Last()}", tag.Name, ResourceType.Tag, order++, new
            {
                tagId = tag.Id, dataType = tag.Type.ToString(), taskId = task.TaskId, isVirtual = true
            }), cancellationToken);
        }
    }

    private static ResourceNode Node(string id, string? parentId, string path, string displayName, ResourceType type, int order, object? metadata = null) => new()
    {
        Id = id, ParentId = parentId, Path = new ResourcePath(path), Name = path.Split('/').Last(),
        DisplayName = displayName, ResourceType = type, SortOrder = order, IsEnabled = true,
        MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata)
    };

    private static string Segment(string value) => string.IsNullOrWhiteSpace(value)
        ? "unnamed" : value.Trim().Replace('/', '-').Replace('\\', '-');
}
