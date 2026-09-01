using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Runtime;

/// <summary>为每个检测任务生成稳定 TagId，供历史、趋势和报警模块共同引用。</summary>
public static class VisionTagCatalog
{
    public static string Connected(string taskId) => $"vision-{taskId}-connected";
    public static string Running(string taskId) => $"vision-{taskId}-running";
    public static string Pass(string taskId) => $"vision-{taskId}-pass";
    public static string CapPresent(string taskId) => $"vision-{taskId}-cap-present";
    public static string MatchScore(string taskId) => $"vision-{taskId}-match-score";
    public static string ProcessingTime(string taskId) => $"vision-{taskId}-processing-ms";
    public static string TotalCount(string taskId) => $"vision-{taskId}-total-count";
    public static string NgCount(string taskId) => $"vision-{taskId}-ng-count";
    public static string PassRate(string taskId) => $"vision-{taskId}-pass-rate";

    public static IReadOnlyList<TagPoint> CreateTagPoints(VisionInspectionTask task) =>
    [
        Create(Connected(task.TaskId), "Camera.Connected", TagDataType.Bool),
        Create(Running(task.TaskId), "Task.Running", TagDataType.Bool),
        Create(Pass(task.TaskId), "Result.Pass", TagDataType.Bool),
        Create(CapPresent(task.TaskId), "Result.CapPresent", TagDataType.Bool),
        Create(MatchScore(task.TaskId), "Result.MatchScore", TagDataType.Float64, 0.001),
        Create(ProcessingTime(task.TaskId), "Result.ProcessingTime", TagDataType.Float64, 0.1),
        Create(TotalCount(task.TaskId), "Statistics.TotalCount", TagDataType.Int64),
        Create(NgCount(task.TaskId), "Statistics.NgCount", TagDataType.Int64),
        Create(PassRate(task.TaskId), "Statistics.PassRate", TagDataType.Float64, 0.01)
    ];

    private static TagPoint Create(string id, string name, TagDataType type, double deadband = 0) => new()
    {
        Id = id, Name = name, Address = id, DataType = type, Access = TagAccess.Read,
        Deadband = deadband, Description = "机器视觉检测运行时数据点"
    };
}
