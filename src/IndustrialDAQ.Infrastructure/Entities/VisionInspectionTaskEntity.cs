using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndustrialDAQ.Infrastructure.Entities;

/// <summary>视觉检测任务持久化实体。</summary>
[Table("vision_inspection_tasks")]
public sealed class VisionInspectionTaskEntity
{
    [Key] public string TaskId { get; set; } = string.Empty;
    public string CameraId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string AlgorithmType { get; set; } = "CapPresenceTemplate";
    public string RoiJson { get; set; } = "{}";
    public string ParametersJson { get; set; } = "{}";
    public string TemplateImagePath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public long Version { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
