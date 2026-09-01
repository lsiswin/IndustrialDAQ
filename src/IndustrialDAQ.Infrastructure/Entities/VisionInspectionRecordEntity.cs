using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndustrialDAQ.Infrastructure.Entities;

/// <summary>视觉单帧检测记录。</summary>
[Table("vision_inspection_records")]
public sealed class VisionInspectionRecordEntity
{
    [Key] public string RecordId { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string CameraId { get; set; } = string.Empty;
    public string FrameId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool IsPass { get; set; }
    public bool CapPresent { get; set; }
    public double MatchScore { get; set; }
    public double ProcessingTimeMilliseconds { get; set; }
    public string? ImagePath { get; set; }
    public string FailureReason { get; set; } = string.Empty;
}
