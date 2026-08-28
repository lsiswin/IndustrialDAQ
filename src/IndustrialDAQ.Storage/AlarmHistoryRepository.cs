// File: AlarmHistoryRepository.cs  Module: Storage  Author: IndustrialDAQ Team
using System.Data;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Infrastructure;
using IndustrialDAQ.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IndustrialDAQ.Storage;

/// <summary>
/// 报警历史仓储 — 使用 EF Core 将报警记录持久化到 SQLite。
/// 支持批量写入和查询优化。
/// </summary>
public sealed class AlarmHistoryRepository
{
    private readonly IDbContextFactory<DaqDbContext> _contextFactory;
    private readonly ILogger<AlarmHistoryRepository> _logger;

    /// <summary>
    /// 初始化报警历史仓储。
    /// </summary>
    public AlarmHistoryRepository(IDbContextFactory<DaqDbContext> contextFactory,
        ILogger<AlarmHistoryRepository> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 保存报警记录到数据库。
    /// </summary>
    /// <param name="record">报警记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SaveAsync(AlarmRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var entity = AlarmHistoryEntity.FromDomain(record);
            entity.CreatedAt = DateTime.UtcNow;

            context.AlarmHistories.Add(entity);
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("报警记录已保存: {AlarmId}", record.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存报警记录失败: {AlarmId}", record.Id);
            throw;
        }
    }

    /// <summary>
    /// 更新报警记录状态。
    /// </summary>
    /// <param name="alarmId">报警 ID。</param>
    /// <param name="status">新状态。</param>
    /// <param name="acknowledgedAt">确认时间（可选）。</param>
    /// <param name="clearedAt">清除时间（可选）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task UpdateStatusAsync(string alarmId, AlarmStatus status,
        DateTime? acknowledgedAt = null, DateTime? clearedAt = null,
        CancellationToken cancellationToken = default, bool resetClearedAt = false)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            // 先用 EF Core 更新
            var entity = await context.AlarmHistories
                .FirstOrDefaultAsync(e => e.AlarmId == alarmId, cancellationToken);

            if (entity is null)
            {
                _logger.LogWarning("报警记录不存在，无法更新: AlarmId={AlarmId}, 目标状态={Status}", alarmId, status);
                return;
            }

            _logger.LogInformation("找到报警记录: AlarmId={AlarmId}, 当前状态={CurrentStatus}, 目标状态={TargetStatus}",
                alarmId, entity.Status, status);

            entity.Status = status.ToString();
            if (acknowledgedAt.HasValue)
                entity.AcknowledgedAt = acknowledgedAt.Value;
            
            if (resetClearedAt)
                entity.ClearedAt = null;
            else if (clearedAt.HasValue)
                entity.ClearedAt = clearedAt.Value;

            int affected = await context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("EF Core 更新完成: AlarmId={AlarmId}, Status={Status}, 受影响行数={Affected}",
                alarmId, status, affected);

            // 如果 EF Core 更新行数为 0，使用原始 SQL 作为保底
            if (affected == 0)
            {
                _logger.LogWarning("EF Core 更新 0 行，使用原始 SQL 保底更新: AlarmId={AlarmId}", alarmId);
                await using var conn = context.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync(cancellationToken);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE AlarmHistories SET Status = @Status WHERE AlarmId = @AlarmId";
                var pStatus = cmd.CreateParameter();
                pStatus.ParameterName = "@Status";
                pStatus.Value = status.ToString();
                cmd.Parameters.Add(pStatus);
                var pAlarmId = cmd.CreateParameter();
                pAlarmId.ParameterName = "@AlarmId";
                pAlarmId.Value = alarmId;
                cmd.Parameters.Add(pAlarmId);

                int sqlAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogInformation("原始 SQL 更新完成: AlarmId={AlarmId}, 受影响行数={Affected}", alarmId, sqlAffected);
            }

            // 验证更新结果
            await using var verifyContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var verifyEntity = await verifyContext.AlarmHistories
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.AlarmId == alarmId, cancellationToken);
            if (verifyEntity is not null)
            {
                _logger.LogInformation("验证数据库状态: AlarmId={AlarmId}, 数据库实际状态={DbStatus}, ClearedAt={ClearedAt}",
                    alarmId, verifyEntity.Status, verifyEntity.ClearedAt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新报警记录状态失败: AlarmId={AlarmId}", alarmId);
            throw;
        }
    }

    /// <summary>
    /// 获取报警历史记录（分页）。
    /// </summary>
    /// <param name="pageNumber">页码（从 1 开始）。</param>
    /// <param name="pageSize">每页大小。</param>
    /// <param name="status">状态筛选（可选）。</param>
    /// <param name="severity">严重程度筛选（可选）。</param>
    /// <param name="tagId">测点 ID 筛选（可选）。</param>
    /// <param name="startTime">开始时间（可选）。</param>
    /// <param name="endTime">结束时间（可选）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>报警记录列表和总数。</returns>
    public async Task<(IReadOnlyList<AlarmRecord> Records, int TotalCount)> GetHistoryAsync(
        int pageNumber = 1, int pageSize = 50,
        AlarmStatus? status = null, AlarmSeverity? severity = null,
        string? tagId = null, DateTime? startTime = null, DateTime? endTime = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var query = context.AlarmHistories.AsQueryable();

            // 应用筛选条件
            if (status.HasValue)
                query = query.Where(e => e.Status == status.Value.ToString());
            if (severity.HasValue)
                query = query.Where(e => e.Severity == severity.Value.ToString());
            if (!string.IsNullOrEmpty(tagId))
                query = query.Where(e => e.TagId == tagId);
            if (startTime.HasValue)
                query = query.Where(e => e.OccurredAt >= startTime.Value);
            if (endTime.HasValue)
                query = query.Where(e => e.OccurredAt <= endTime.Value);

            // 获取总数
            int totalCount = await query.CountAsync(cancellationToken);

            // 分页查询
            var records = await query
                .OrderByDescending(e => e.OccurredAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(e => e.ToDomain())
                .ToListAsync(cancellationToken);

            return (records, totalCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询报警历史失败");
            throw;
        }
    }

    /// <summary>
    /// 获取活跃报警列表。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>活跃报警记录列表。</returns>
    public async Task<IReadOnlyList<AlarmRecord>> GetActiveAlarmsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var records = await context.AlarmHistories
                .Where(e => e.Status == AlarmStatus.Active.ToString() || e.Status == AlarmStatus.Acknowledged.ToString())
                .OrderByDescending(e => e.OccurredAt)
                .Select(e => e.ToDomain())
                .ToListAsync(cancellationToken);

            return records;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询活跃报警失败");
            throw;
        }
    }

    /// <summary>
    /// 关闭上次 Runtime 异常退出后遗留的活跃报警。
    /// 新进程启动后规则引擎会依据当前实时值重新生成新的报警周期，
    /// 因此旧进程中的 Active/Acknowledged 记录不能继续占用活跃状态。
    /// </summary>
    public async Task<int> ReconcileOrphanedActiveAlarmsAsync(
        DateTime runtimeStartedAtUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var activeStatuses = new[]
            {
                AlarmStatus.Active.ToString(),
                AlarmStatus.Acknowledged.ToString()
            };

            var orphanedRecords = await context.AlarmHistories
                .Where(entity => activeStatuses.Contains(entity.Status)
                    && entity.OccurredAt < runtimeStartedAtUtc)
                .ToListAsync(cancellationToken);

            foreach (var entity in orphanedRecords)
            {
                entity.Status = AlarmStatus.Cleared.ToString();
                entity.ClearedAt = runtimeStartedAtUtc;
            }

            if (orphanedRecords.Count > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
                _logger.LogWarning(
                    "Runtime 启动对账已关闭 {Count} 条遗留活跃报警，关闭时间: {RuntimeStartedAtUtc}",
                    orphanedRecords.Count,
                    runtimeStartedAtUtc);
            }

            return orphanedRecords.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Runtime 启动时对账遗留活跃报警失败");
            throw;
        }
    }

    /// <summary>
    /// 获取报警统计信息。
    /// </summary>
    /// <param name="startTime">开始时间。</param>
    /// <param name="endTime">结束时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>报警统计信息。</returns>
    public async Task<AlarmStatistics> GetStatisticsAsync(
        DateTime startTime, DateTime endTime,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var query = context.AlarmHistories
                .Where(e => e.OccurredAt >= startTime && e.OccurredAt <= endTime);

            var statistics = new AlarmStatistics
            {
                TotalCount = await query.CountAsync(cancellationToken),
                ActiveCount = await query.CountAsync(e => e.Status == AlarmStatus.Active.ToString(), cancellationToken),
                AcknowledgedCount = await query.CountAsync(e => e.Status == AlarmStatus.Acknowledged.ToString(), cancellationToken),
                ClearedCount = await query.CountAsync(e => e.Status == AlarmStatus.Cleared.ToString(), cancellationToken),
                CriticalCount = await query.CountAsync(e => e.Severity == AlarmSeverity.Critical.ToString(), cancellationToken),
                WarningCount = await query.CountAsync(e => e.Severity == AlarmSeverity.Warning.ToString(), cancellationToken),
                InfoCount = await query.CountAsync(e => e.Severity == AlarmSeverity.Info.ToString(), cancellationToken)
            };

            return statistics;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取报警统计失败");
            throw;
        }
    }

    /// <summary>
    /// 清理过期的报警历史记录。
    /// </summary>
    /// <param name="retentionDays">保留天数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除的记录数。</returns>
    public async Task<int> CleanupAsync(int retentionDays = 90,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

            var expiredRecords = await context.AlarmHistories
                .Where(e => e.ClearedAt.HasValue && e.ClearedAt.Value < cutoffDate)
                .ToListAsync(cancellationToken);

            int count = expiredRecords.Count;
            if (count > 0)
            {
                context.AlarmHistories.RemoveRange(expiredRecords);
                await context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("已清理 {Count} 条过期报警记录", count);
            }

            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理过期报警记录失败");
            throw;
        }
    }
}

/// <summary>
/// 报警统计信息。
/// </summary>
public sealed class AlarmStatistics
{
    /// <summary>总数。</summary>
    public int TotalCount { get; set; }

    /// <summary>活跃报警数。</summary>
    public int ActiveCount { get; set; }

    /// <summary>已确认报警数。</summary>
    public int AcknowledgedCount { get; set; }

    /// <summary>已清除报警数。</summary>
    public int ClearedCount { get; set; }

    /// <summary>严重报警数。</summary>
    public int CriticalCount { get; set; }

    /// <summary>警告报警数。</summary>
    public int WarningCount { get; set; }

    /// <summary>信息报警数。</summary>
    public int InfoCount { get; set; }
}
