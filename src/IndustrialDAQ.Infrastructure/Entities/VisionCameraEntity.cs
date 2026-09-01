using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndustrialDAQ.Infrastructure.Entities;

/// <summary>视觉相机持久化实体。</summary>
[Table("vision_cameras")]
public sealed class VisionCameraEntity
{
    [Key] public string CameraId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DriverType { get; set; } = "Directory";
    public string ConnectionConfigJson { get; set; } = "{}";
    public string TriggerMode { get; set; } = "Continuous";
    public bool IsEnabled { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
