using IndustrialDAQ.Infrastructure;
using IndustrialDAQ.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndustrialDAQ.UI.Services;

/// <summary>按系统设置定期清理测点历史和已结束报警历史。</summary>
public sealed class HistoricalDataRetentionService : IHostedService
{
    private readonly IDbContextFactory<DaqDbContext> _factory;
    private readonly AlarmHistoryRepository _alarmRepository;
    private readonly RuntimeSettingsService _settings;
    private readonly SecurityAuditService _audit;
    private readonly ILogger<HistoricalDataRetentionService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public HistoricalDataRetentionService(IDbContextFactory<DaqDbContext> factory, AlarmHistoryRepository alarmRepository, RuntimeSettingsService settings, SecurityAuditService audit, ILogger<HistoricalDataRetentionService> logger) =>
        (_factory, _alarmRepository, _settings, _audit, _logger) = (factory, alarmRepository, settings, audit, logger);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _task = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_task is not null) try { await _task.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
        _cts?.Dispose();
    }

    public async Task<(int History, int Alarms)> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var days = _settings.Current.HistoryRetentionDays;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToString("O");
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var historyCount = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM historical_records WHERE Timestamp < {cutoff}", cancellationToken);
        var alarmCount = await _alarmRepository.CleanupAsync(days, cancellationToken);
        await _audit.RecordAsync("system", "System", "HistoryRetentionCleanup", "System/Storage", $"RetentionDays={days};History={historyCount};Alarms={alarmCount}", true);
        _logger.LogInformation("历史保留清理完成，测点 {HistoryCount} 条，报警 {AlarmCount} 条", historyCount, alarmCount);
        return (historyCount, alarmCount);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await CleanupAsync(cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "历史保留清理任务失败"); }
            await Task.Delay(TimeSpan.FromDays(1), cancellationToken);
        }
    }
}
