using System.Text.Json;
using IndustrialDAQ.Infrastructure;
using IndustrialDAQ.Infrastructure.Entities;
using IndustrialDAQ.Vision.Abstractions;
using IndustrialDAQ.Vision.Models;
using Microsoft.EntityFrameworkCore;

namespace IndustrialDAQ.Vision.Storage;

/// <summary>基于当前业务数据库的视觉配置仓储，兼容 SQLite 与 PostgreSQL。</summary>
public sealed class VisionConfigurationRepository : IVisionConfigurationRepository
{
    private readonly IDbContextFactory<DaqDbContext> _factory;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    public VisionConfigurationRepository(IDbContextFactory<DaqDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<VisionCameraConfig>> LoadCamerasAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.VisionCameras.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToArray();
    }

    public async Task<IReadOnlyList<VisionInspectionTask>> LoadTasksAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.VisionInspectionTasks.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToArray();
    }

    public async Task UpsertCameraAsync(VisionCameraConfig camera, CancellationToken cancellationToken = default)
    {
        Validate(camera);
        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.VisionCameras.FirstOrDefaultAsync(item => item.CameraId == camera.CameraId, cancellationToken)
                     ?? new VisionCameraEntity { CameraId = camera.CameraId };
        if (db.Entry(entity).State == EntityState.Detached) db.VisionCameras.Add(entity);
        entity.Name = camera.Name;
        entity.DriverType = camera.DriverType;
        entity.ConnectionConfigJson = JsonSerializer.Serialize(new CameraConnection(camera.ImageDirectory, camera.IntervalMilliseconds, camera.Loop));
        entity.TriggerMode = camera.TriggerMode.ToString();
        entity.IsEnabled = camera.IsEnabled;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertTaskAsync(VisionInspectionTask task, CancellationToken cancellationToken = default)
    {
        Validate(task);
        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.VisionInspectionTasks.FirstOrDefaultAsync(item => item.TaskId == task.TaskId, cancellationToken)
                     ?? new VisionInspectionTaskEntity { TaskId = task.TaskId };
        if (db.Entry(entity).State == EntityState.Detached) db.VisionInspectionTasks.Add(entity);
        entity.CameraId = task.CameraId;
        entity.Name = task.Name;
        entity.ProductCode = task.ProductCode;
        entity.AlgorithmType = task.AlgorithmType;
        entity.RoiJson = JsonSerializer.Serialize(task.Roi);
        entity.ParametersJson = JsonSerializer.Serialize(new TaskParameters(task.MatchThreshold, task.SaveNgImage));
        entity.TemplateImagePath = task.TemplateImagePath;
        entity.IsEnabled = task.IsEnabled;
        entity.Version = task.Version;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await db.VisionInspectionTasks.Where(item => item.TaskId == taskId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task SaveResultAsync(VisionInspectionResult result, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        db.VisionInspectionRecords.Add(new VisionInspectionRecordEntity
        {
            RecordId = result.RecordId, TaskId = result.TaskId, CameraId = result.CameraId,
            FrameId = result.FrameId, TimestampUtc = result.Timestamp.UtcDateTime,
            IsPass = result.IsPass, CapPresent = result.CapPresent, MatchScore = result.MatchScore,
            ProcessingTimeMilliseconds = result.ProcessingTimeMilliseconds,
            ImagePath = result.ImagePath, FailureReason = result.FailureReason
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady) return;
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var sql = db.Database.IsNpgsql() ? PostgreSqlSchema : SqliteSchema;
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            _schemaReady = true;
        }
        finally { _schemaLock.Release(); }
    }

    private static VisionCameraConfig ToDomain(VisionCameraEntity entity)
    {
        var connection = JsonSerializer.Deserialize<CameraConnection>(entity.ConnectionConfigJson) ?? new("", 1000, true);
        return new VisionCameraConfig
        {
            CameraId = entity.CameraId, Name = entity.Name, DriverType = entity.DriverType,
            ImageDirectory = connection.ImageDirectory, IntervalMilliseconds = connection.IntervalMilliseconds,
            Loop = connection.Loop, TriggerMode = Enum.TryParse<VisionTriggerMode>(entity.TriggerMode, true, out var mode) ? mode : VisionTriggerMode.Continuous,
            IsEnabled = entity.IsEnabled
        };
    }

    private static VisionInspectionTask ToDomain(VisionInspectionTaskEntity entity)
    {
        var roi = JsonSerializer.Deserialize<VisionRoi>(entity.RoiJson) ?? VisionRoi.FullFrame;
        var parameters = JsonSerializer.Deserialize<TaskParameters>(entity.ParametersJson) ?? new(0.8, true);
        return new VisionInspectionTask
        {
            TaskId = entity.TaskId, CameraId = entity.CameraId, Name = entity.Name,
            ProductCode = entity.ProductCode, AlgorithmType = entity.AlgorithmType,
            Roi = roi, MatchThreshold = parameters.MatchThreshold, SaveNgImage = parameters.SaveNgImage,
            TemplateImagePath = entity.TemplateImagePath, IsEnabled = entity.IsEnabled, Version = entity.Version
        };
    }

    private static void Validate(VisionCameraConfig camera)
    {
        if (string.IsNullOrWhiteSpace(camera.CameraId) || string.IsNullOrWhiteSpace(camera.Name))
            throw new InvalidOperationException("视觉相机必须包含标识和名称。");
        if (camera.IntervalMilliseconds < 50) throw new InvalidOperationException("模拟相机采集周期不能小于 50ms。");
    }

    private static void Validate(VisionInspectionTask task)
    {
        if (string.IsNullOrWhiteSpace(task.TaskId) || string.IsNullOrWhiteSpace(task.CameraId) || string.IsNullOrWhiteSpace(task.Name))
            throw new InvalidOperationException("视觉任务必须包含任务标识、相机标识和名称。");
        if (!task.Roi.IsValid) throw new InvalidOperationException("瓶盖检测 ROI 必须位于图像范围内。");
        if (task.MatchThreshold is < 0 or > 1) throw new InvalidOperationException("匹配阈值必须位于 0 到 1 之间。");
    }

    private sealed record CameraConnection(string ImageDirectory, int IntervalMilliseconds, bool Loop);
    private sealed record TaskParameters(double MatchThreshold, bool SaveNgImage);

    private const string SqliteSchema = """
        CREATE TABLE IF NOT EXISTS vision_cameras (CameraId TEXT PRIMARY KEY, Name TEXT NOT NULL, DriverType TEXT NOT NULL, ConnectionConfigJson TEXT NOT NULL, TriggerMode TEXT NOT NULL, IsEnabled INTEGER NOT NULL, UpdatedAtUtc TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_vision_cameras_IsEnabled ON vision_cameras (IsEnabled);
        CREATE TABLE IF NOT EXISTS vision_inspection_tasks (TaskId TEXT PRIMARY KEY, CameraId TEXT NOT NULL, Name TEXT NOT NULL, ProductCode TEXT NOT NULL, AlgorithmType TEXT NOT NULL, RoiJson TEXT NOT NULL, ParametersJson TEXT NOT NULL, TemplateImagePath TEXT NOT NULL, IsEnabled INTEGER NOT NULL, Version INTEGER NOT NULL, UpdatedAtUtc TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_vision_inspection_tasks_CameraId ON vision_inspection_tasks (CameraId);
        CREATE INDEX IF NOT EXISTS IX_vision_inspection_tasks_IsEnabled ON vision_inspection_tasks (IsEnabled);
        CREATE TABLE IF NOT EXISTS vision_inspection_records (RecordId TEXT PRIMARY KEY, TaskId TEXT NOT NULL, CameraId TEXT NOT NULL, FrameId TEXT NOT NULL, TimestampUtc TEXT NOT NULL, IsPass INTEGER NOT NULL, CapPresent INTEGER NOT NULL, MatchScore REAL NOT NULL, ProcessingTimeMilliseconds REAL NOT NULL, ImagePath TEXT NULL, FailureReason TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_vision_inspection_records_TaskId_TimestampUtc ON vision_inspection_records (TaskId, TimestampUtc);
        """;

    private const string PostgreSqlSchema = """
        CREATE TABLE IF NOT EXISTS vision_cameras ("CameraId" text PRIMARY KEY, "Name" text NOT NULL, "DriverType" text NOT NULL, "ConnectionConfigJson" text NOT NULL, "TriggerMode" text NOT NULL, "IsEnabled" boolean NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
        CREATE INDEX IF NOT EXISTS "IX_vision_cameras_IsEnabled" ON vision_cameras ("IsEnabled");
        CREATE TABLE IF NOT EXISTS vision_inspection_tasks ("TaskId" text PRIMARY KEY, "CameraId" text NOT NULL, "Name" text NOT NULL, "ProductCode" text NOT NULL, "AlgorithmType" text NOT NULL, "RoiJson" text NOT NULL, "ParametersJson" text NOT NULL, "TemplateImagePath" text NOT NULL, "IsEnabled" boolean NOT NULL, "Version" bigint NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
        CREATE INDEX IF NOT EXISTS "IX_vision_inspection_tasks_CameraId" ON vision_inspection_tasks ("CameraId");
        CREATE INDEX IF NOT EXISTS "IX_vision_inspection_tasks_IsEnabled" ON vision_inspection_tasks ("IsEnabled");
        CREATE TABLE IF NOT EXISTS vision_inspection_records ("RecordId" text PRIMARY KEY, "TaskId" text NOT NULL, "CameraId" text NOT NULL, "FrameId" text NOT NULL, "TimestampUtc" timestamp with time zone NOT NULL, "IsPass" boolean NOT NULL, "CapPresent" boolean NOT NULL, "MatchScore" double precision NOT NULL, "ProcessingTimeMilliseconds" double precision NOT NULL, "ImagePath" text NULL, "FailureReason" text NOT NULL);
        CREATE INDEX IF NOT EXISTS "IX_vision_inspection_records_TaskId_TimestampUtc" ON vision_inspection_records ("TaskId", "TimestampUtc");
        """;
}
