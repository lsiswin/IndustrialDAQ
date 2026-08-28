
using IndustrialDAQ.Alarm.Center;
using IndustrialDAQ.Alarm.RuleEngine;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndustrialDAQ.Alarm;

/// <summary>
/// 报警管理门面。
/// 对 UI 维持旧的事件和 API 形态，但内部已切换到
/// AlarmDefinitionRepository -> RuleEngineService -> AlarmStateMachineService -> AlarmCenter
/// 这条新链路。
/// </summary>
public sealed class AlarmManager : IHostedService
{
    private readonly IAlarmCenter _alarmCenter;
    private readonly IAlarmCenterEventBus _eventBus;
    private readonly AlarmHistoryRepository _repository;
    private readonly IAlarmDefinitionRepository _definitionRepository;
    private readonly IRuleEngineService _ruleEngineService;
    private readonly ILogger<AlarmManager> _logger;

    /// <summary>报警事件消费任务。</summary>
    private Task? _consumeTask;
    private CancellationTokenSource? _cts;

    /// <summary>报警事件触发（供 UI 订阅）。</summary>
    public event EventHandler<AlarmEventArgs>? AlarmTriggered;

    /// <summary>报警确认事件（供 UI 订阅）。</summary>
    public event EventHandler<AlarmEventArgs>? AlarmAcknowledged;

    /// <summary>报警恢复事件（供 UI 订阅）。</summary>
    public event EventHandler<AlarmEventArgs>? AlarmCleared;

    /// <summary>实时报警列表变更事件。</summary>
    public event EventHandler? ActiveAlarmsChanged;

    /// <summary>
    /// 初始化报警管理服务。
    /// </summary>
    public AlarmManager(
        IAlarmCenter alarmCenter,
        IAlarmCenterEventBus eventBus,
        AlarmHistoryRepository repository,
        IAlarmDefinitionRepository definitionRepository,
        IRuleEngineService ruleEngineService,
        ILogger<AlarmManager> logger)
    {
        _alarmCenter = alarmCenter ?? throw new ArgumentNullException(nameof(alarmCenter));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _definitionRepository = definitionRepository ?? throw new ArgumentNullException(nameof(definitionRepository));
        _ruleEngineService = ruleEngineService ?? throw new ArgumentNullException(nameof(ruleEngineService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 注册报警规则到新链路的定义仓储，并触发规则引擎热重载。
    /// </summary>
    public void RegisterRule(AlarmDefinition rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _definitionRepository.UpsertAsync(rule, CancellationToken.None).GetAwaiter().GetResult();
        _ruleEngineService.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
        _logger.LogInformation("已注册报警规则到新链路: {RuleId}", rule.RuleId);
    }

    /// <summary>
    /// 批量注册报警规则到新链路，并触发一次规则引擎热重载。
    /// </summary>
    public void RegisterRules(IEnumerable<AlarmDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var materialized = rules.ToArray();
        foreach (var rule in materialized)
        {
            _definitionRepository.UpsertAsync(rule, CancellationToken.None).GetAwaiter().GetResult();
        }

        _ruleEngineService.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
        _logger.LogInformation("已批量注册 {Count} 条报警规则到新链路", materialized.Length);
    }

    /// <summary>
    /// 确认报警。
    /// </summary>
    /// <param name="alarmId">报警 ID。</param>
    /// <returns>是否成功确认。</returns>
    public bool AcknowledgeAlarm(string alarmId)
    {
        return _alarmCenter
            .AcknowledgeAsync(alarmId, "UI", CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// 确认所有活跃报警。
    /// </summary>
    public void AcknowledgeAllAlarms()
    {
        _alarmCenter
            .AcknowledgeAllAsync("UI", CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// 获取报警条件仍然成立的实时报警列表。
    /// 已清除但等待操作员确认的记录仍由报警中心保留，供报警日志和确认流程使用，
    /// 但不能继续计入活动报警或驱动趋势图红色标记。
    /// </summary>
    public IReadOnlyList<AlarmRecord> GetActiveAlarms()
    {
        return _alarmCenter
            .GetCurrentAlarms()
            .Where(static alarm => alarm.Status is AlarmStatus.Active or AlarmStatus.Acknowledged)
            .OrderByDescending(a => a.OccurredAt)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// 获取报警历史记录。
    /// </summary>
    public async Task<(IReadOnlyList<AlarmRecord> Records, int TotalCount)> GetHistoryAsync(
        int pageNumber = 1, int pageSize = 50,
        AlarmStatus? status = null, AlarmSeverity? severity = null,
        string? tagId = null, DateTime? startTime = null, DateTime? endTime = null,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetHistoryAsync(pageNumber, pageSize, status, severity,
            tagId, startTime, endTime, cancellationToken);
    }

    /// <summary>
    /// 获取报警统计信息。
    /// </summary>
    public async Task<AlarmStatistics> GetStatisticsAsync(
        DateTime startTime, DateTime endTime,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetStatisticsAsync(startTime, endTime, cancellationToken);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 先结束上次进程遗留的活跃记录，再开始接收本次 Runtime 的报警事件。
        // 如果实时条件仍成立，规则引擎会创建新的唯一报警周期。
        var runtimeStartedAtUtc = DateTime.UtcNow;
        await _repository
            .ReconcileOrphanedActiveAlarmsAsync(runtimeStartedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _consumeTask = Task.Run(() => ConsumeEventsAsync(_cts.Token), _cts.Token);
        _logger.LogInformation("报警管理门面已启动，事件源切换到 AlarmCenter");
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("报警管理服务正在停止...");
        _cts?.Cancel();

        if (_consumeTask is not null)
        {
            try { await _consumeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _cts?.Dispose();
        _logger.LogInformation("报警管理服务已停止");
    }

    /// <summary>
    /// 消费报警事件，更新实时列表并持久化到数据库。
    /// </summary>
    private async Task ConsumeEventsAsync(CancellationToken ct)
    {
        _logger.LogDebug("报警管理服务消费循环已启动");
        try
        {
            await foreach (var alarmEvent in _eventBus.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                _logger.LogDebug("收到报警中心事件: {AlarmId}, 类型: {EventType}, 规则: {RuleId}",
                    alarmEvent.Record.Id, alarmEvent.EventType, alarmEvent.Record.RuleId);

                try
                {
                    await ProcessAlarmEventAsync(alarmEvent, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "处理报警事件失败: {AlarmId}", alarmEvent.Record.Id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "报警管理服务消费循环异常");
        }
    }

    /// <summary>
    /// 处理单个报警中心事件，并向旧 UI 事件模型做兼容分发。
    /// </summary>
    private Task ProcessAlarmEventAsync(AlarmCenterEvent alarmEvent, CancellationToken ct)
    {
        var record = alarmEvent.Record;

        _logger.LogInformation("处理报警中心事件: AlarmId={AlarmId}, EventType={EventType}, RuleId={RuleId}",
            record.Id, alarmEvent.EventType, record.RuleId);

        switch (alarmEvent.EventType)
        {
            case AlarmCenterEventType.Raised:
                AlarmTriggered?.Invoke(this, new AlarmEventArgs(record));
                break;

            case AlarmCenterEventType.Acknowledged:
                AlarmAcknowledged?.Invoke(this, new AlarmEventArgs(record));
                break;

            case AlarmCenterEventType.Cleared:
            case AlarmCenterEventType.Closed:
                AlarmCleared?.Invoke(this, new AlarmEventArgs(record));
                break;

            case AlarmCenterEventType.Suppressed:
            case AlarmCenterEventType.Shelved:
                break;
        }

        ActiveAlarmsChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 报警事件参数。
/// </summary>
public sealed class AlarmEventArgs : EventArgs
{
    /// <summary>报警记录。</summary>
    public AlarmRecord Record { get; }

    public AlarmEventArgs(AlarmRecord record)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
    }
}
