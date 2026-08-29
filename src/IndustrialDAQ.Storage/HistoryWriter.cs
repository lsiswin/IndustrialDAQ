// File: HistoryWriter.cs  Module: Storage Engine  Author: IndustrialDAQ Team
using System.Collections.Concurrent;
using System.Threading.Channels;
using IndustrialDAQ.Core;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Infrastructure;
using IndustrialDAQ.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndustrialDAQ.Storage;

/// <summary>
/// 历史数据写入器 — 从采集管道消费测点值，执行死区压缩后批量写入 SQLite。
/// 作为 <see cref="IHostedService"/> 运行，支持优雅停止时刷新残留批次。
/// </summary>
public sealed class HistoryWriter : IHostedService
{
    private readonly ChannelReader<TagValue> _reader;
    private readonly RealTimeStore _realTimeStore;
    private readonly IDbContextFactory<DaqDbContext> _dbFactory;
    private readonly ILogger<HistoryWriter> _logger;

    /// <summary>测点配置注册表 — Key=TagId, Value=TagPoint（含死区阈值）。</summary>
    private readonly ConcurrentDictionary<string, TagPoint> _tagConfigs = new();

    /// <summary>上一次存入历史库的测点值 — Key=TagId, Value=数值(double)。</summary>
    private readonly ConcurrentDictionary<string, double> _lastPersisted = new();

    private CancellationTokenSource? _cts;
    private Task? _consumeTask;

    private const int BatchSize = 1_000;
    private const int FlushIntervalMs = 1_000;

    /// <summary>
    /// 初始化历史写入器。
    /// </summary>
    /// <param name="channel">采集管道（消费端）</param>
    /// <param name="realTimeStore">实时数据库</param>
    /// <param name="dbFactory">DbContext 工厂（线程安全）</param>
    /// <param name="logger">日志记录器</param>
    public HistoryWriter(
        AcquisitionChannel channel,
        RealTimeStore realTimeStore,
        IDbContextFactory<DaqDbContext> dbFactory,
        ILogger<HistoryWriter> logger)
    {
        _reader = (channel ?? throw new ArgumentNullException(nameof(channel))).Reader;
        _realTimeStore = realTimeStore ?? throw new ArgumentNullException(nameof(realTimeStore));
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 注册测点配置（设备启动时调用），用于死区压缩判断。
    /// </summary>
    public void RegisterTags(IEnumerable<TagPoint> tags)
    {
        foreach (TagPoint tag in tags)
        {
            _tagConfigs[tag.Id] = tag;
        }
        _logger.LogDebug("已注册 {Count} 个测点配置", tags.Count());
    }

    /// <summary>
    /// 注销测点配置（设备停止时调用）。
    /// </summary>
    public void UnregisterTags(IEnumerable<string> tagIds)
    {
        foreach (string id in tagIds)
        {
            _tagConfigs.TryRemove(id, out _);
            _lastPersisted.TryRemove(id, out _);
        }
        _logger.LogDebug("已注销 {Count} 个测点配置", tagIds.Count());
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _consumeTask = Task.Run(() => ConsumeAsync(_cts.Token), _cts.Token);
        _logger.LogInformation("历史写入器已启动 (批量={BatchSize}, 刷新间隔={FlushMs}ms)",
            BatchSize, FlushIntervalMs);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("历史写入器正在停止...");
        _cts?.Cancel();

        if (_consumeTask is not null)
        {
            try { await _consumeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 预期内 */ }
        }

        _cts?.Dispose();
        _logger.LogInformation("历史写入器已停止");
    }

    /// <summary>
    /// 主消费循环 — 读取管道 → 实时库 → 死区判断 → 批量写入历史库。
    /// </summary>
    private async Task ConsumeAsync(CancellationToken ct)
    {
        var batch = new List<TagValue>(BatchSize);
        var lastFlush = DateTimeOffset.UtcNow;

        try
        {
            await foreach (TagValue value in _reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // 步骤 1: 无条件写入实时数据库
                _realTimeStore.Update(value);

                // 步骤 2: 死区压缩判断
                if (ShouldPersistToHistory(value))
                {
                    batch.Add(value);
                }

                // 步骤 3: 达到批量阈值或时间间隔则刷新
                bool countReached = batch.Count >= BatchSize;
                bool timeReached = batch.Count > 0 &&
                    (DateTimeOffset.UtcNow - lastFlush).TotalMilliseconds >= FlushIntervalMs;

                if (countReached || timeReached)
                {
                    await FlushBatchAsync(batch, ct).ConfigureAwait(false);
                    batch.Clear();
                    lastFlush = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常退出 — 刷新残留批次
            if (batch.Count > 0)
            {
                _logger.LogInformation("退出前刷新残留批次 ({Count} 条)", batch.Count);
                await FlushBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 死区压缩判断 — 只有新值与上次存入值的变化绝对值超过死区阈值时才允许存入。
    /// 对于非数值类型（Bool, String）或首次存入，直接允许。
    /// </summary>
    private bool ShouldPersistToHistory(TagValue value)
    {
        // 不良质量不存入历史库
        if (value.Quality == Quality.Bad)
            return false;

        // 没有配置信息或值为 null，跳过
        if (!_tagConfigs.TryGetValue(value.TagId, out TagPoint? config) || value.Value is null)
            return false;

        // 尝试将值转为 double 进行死区比较
        if (!TryConvertToDouble(value.Value, out double numericValue))
        {
            // 布尔/字符串等非数值类型：始终允许存入（首次存入后更新状态）
            _lastPersisted[value.TagId] = 0; // 占位，表示已存入过
            return true;
        }

        // 首次存入该测点：允许并记录基准值
        if (!_lastPersisted.TryGetValue(value.TagId, out double lastValue))
        {
            _lastPersisted[value.TagId] = numericValue;
            return true;
        }

        double change = Math.Abs(numericValue - lastValue);

        // 变化未超过死区阈值，丢弃
        if (change <= config.Deadband)
            return false;

        // 变化超过阈值，更新基准值并允许存入
        _lastPersisted[value.TagId] = numericValue;
        return true;
    }

    /// <summary>
    /// 将批次数据写入 SQLite 历史库。使用 DbContextFactory 保证线程安全。
    /// </summary>
    private async Task FlushBatchAsync(List<TagValue> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        try
        {
            // DbContextFactory 每次创建独立实例，天然线程安全
            await using DaqDbContext db = await _dbFactory.CreateDbContextAsync(ct)
                .ConfigureAwait(false);

            var records = new List<HistoricalRecord>(batch.Count);
            foreach (TagValue value in batch)
            {
                records.Add(MapToRecord(value));
            }

            db.HistoricalRecords.AddRange(records);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogDebug("已写入 {Count} 条历史记录", records.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量写入历史库失败 ({Count} 条记录丢失)", batch.Count);
            // 工业场景：写入失败不阻塞采集管道，仅记录日志
        }
    }

    /// <summary>
    /// 将 <see cref="TagValue"/> 映射为 <see cref="HistoricalRecord"/> 实体。
    /// </summary>
    private static HistoricalRecord MapToRecord(TagValue value)
    {
        var encoded = HistoricalValueCodec.Serialize(value.Value, value.DataType);
        return new HistoricalRecord
        {
            TagId = value.TagId,
            TagName = value.TagName,
            Value = encoded.Value,
            ValueType = encoded.ValueType,
            QualityCode = (byte)value.Quality,
            Timestamp = value.Timestamp.ToString("O")   // ISO 8601 往返格式
        };
    }

    /// <summary>
    /// 安全尝试将 object 值转为 double（处理 bool、数值、字符串等）。
    /// </summary>
    private static bool TryConvertToDouble(object value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case short s:
                result = s;
                return true;
            case byte b:
                result = b;
                return true;
            case bool:
                result = 0;
                return false;  // 布尔值不参与死区比较
            case string str when double.TryParse(str, out double parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
