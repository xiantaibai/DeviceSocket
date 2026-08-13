using DeviceSocket.Cluster.Contracts.Commands;
using DeviceSocket.Cluster.Contracts.Devices;
using DeviceSocket.Cluster.Contracts.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using System.Diagnostics;

namespace DeviceSocket.Cluster.Grains.Scheduling;

/// <summary>
/// 使用内存最小堆调度一个固定设备分片，并用持久化扫描和单个 Reminder 恢复 Silo 故障期间丢失的 Timer。
/// </summary>
public sealed partial class DeviceCommandSchedulerGrain(
    [PersistentState("scheduler", "SchedulerStore")] IPersistentState<DeviceCommandSchedulerGrainState> state,
    ICommandSchedulingStore schedulingStore,
    IOptions<DeviceCommandSchedulingOptions> schedulingOptions,
    ILogger<DeviceCommandSchedulerGrain> logger)
    : Grain, IDeviceCommandSchedulerGrain, IRemindable
{
    private const string RecoveryReminderName = "CommandScheduleRecovery";
    private const int CleanupConcurrency = 16;
    private readonly PriorityQueue<ScheduledEntry, DateTimeOffset> _queue = new();
    private readonly Dictionary<string, ScheduledEntry> _latestByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceCommandSchedulingOptions _options = schedulingOptions.Value;
    private IGrainTimer? _timer;
    private bool _started;

    /// <summary>
    /// 激活时创建一个可动态调整的 one-shot Timer；Timer 本身不承担持久化恢复。
    /// </summary>
    /// <param name="cancellationToken">激活取消信号。</param>
    /// <returns>激活准备任务。</returns>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _timer = this.RegisterGrainTimer(
            OnTimerAsync,
            new GrainTimerCreationOptions(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)
            {
                Interleave = false,
                KeepAlive = true
            });
        return base.OnActivateAsync(cancellationToken);
    }

    /// <summary>
    /// 释放激活内 Timer；持久化 Reminder 和 SQL 下一动作仍可在其他激活上恢复。
    /// </summary>
    /// <param name="reason">Orleans 提供的停用原因。</param>
    /// <param name="cancellationToken">停用取消信号。</param>
    /// <returns>停用清理任务。</returns>
    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    /// <summary>
    /// 幂等启动分片、注册固定 Reminder 并立即恢复近期任务。
    /// </summary>
    /// <returns>启动后的分片快照。</returns>
    public async Task<DeviceCommandSchedulerSnapshot> EnsureStartedAsync()
    {
        await EnsureStartedCoreAsync();
        return CreateSnapshot();
    }

    /// <summary>
    /// 接收设备最新调度版本并压入最小堆；同设备旧版本不会替换新版本。
    /// </summary>
    /// <param name="schedule">设备编号、版本和下一动作时间。</param>
    public async Task ScheduleAsync(DeviceCommandSchedule schedule)
    {
        if (schedule is null || string.IsNullOrWhiteSpace(schedule.DeviceId) || schedule.ScheduleVersion <= 0)
            return;

        await EnsureStartedCoreAsync();
        AddOrUpdate(schedule.DeviceId, schedule.ScheduleVersion, schedule.DueAt);
        ArmTimer();
    }

    /// <summary>
    /// 从 DeviceGrain 和 CommandGrain 类型表恢复近期任务及中断在入队阶段的命令。
    /// </summary>
    /// <returns>扫描后的分片快照。</returns>
    public async Task<DeviceCommandSchedulerSnapshot> RecoverAsync()
    {
        await EnsureReminderAsync();
        await RecoverCoreAsync();
        _started = true;
        return CreateSnapshot();
    }

    /// <summary>
    /// 查询当前分片内存任务数量和持久化统计。
    /// </summary>
    /// <returns>当前分片快照。</returns>
    public Task<DeviceCommandSchedulerSnapshot> GetSnapshotAsync() => Task.FromResult(CreateSnapshot());

    /// <summary>
    /// 处理本分片唯一恢复 Reminder；Reminder 只负责重建调度，不直接为每条命令计时。
    /// </summary>
    /// <param name="reminderName">Reminder 名称。</param>
    /// <param name="status">Orleans 提供的提醒执行状态。</param>
    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, RecoveryReminderName, StringComparison.Ordinal))
            return;

        await RecoverCoreAsync();
        _started = true;
    }

    /// <summary>
    /// 首次调用时初始化持久化状态、Reminder 和恢复窗口；后续调用只返回。
    /// </summary>
    private async Task EnsureStartedCoreAsync()
    {
        if (_started)
            return;

        await EnsureReminderAsync();
        await RecoverCoreAsync();
        _started = true;
    }

    /// <summary>
    /// 注册或续订当前分片唯一的持久化恢复 Reminder，并按分片编号打散首次触发时间。
    /// </summary>
    private async Task EnsureReminderAsync()
    {
        var shardId = GetShardId();
        var jitterSeconds = shardId % 30;
        await this.RegisterOrUpdateReminder(
            RecoveryReminderName,
            TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(jitterSeconds)),
            _options.RecoveryPeriod);
    }

    /// <summary>
    /// 读取足以覆盖百万设备均匀分片峰值的近期窗口，并异步修复未完成入队。
    /// </summary>
    private async Task RecoverCoreAsync()
    {
        var startedAt = Stopwatch.GetTimestamp();
        var now = DateTimeOffset.UtcNow;
        var shardId = GetShardId();
        var take = checked(_options.BatchSize * _options.MaxBatchesPerTick);
        try
        {
            var dueDevices = await schedulingStore.ReadDueDevicesAsync(
                shardId,
                now.Add(_options.RecoveryHorizon),
                take);
            foreach (var item in dueDevices)
                AddOrUpdate(item.DeviceId, item.ScheduleVersion, item.DueAt);

            var pendingCommands = await schedulingStore.ReadPendingEnqueuesAsync(shardId, now, take);
            foreach (var item in pendingCommands)
            {
                await GrainFactory.GetGrain<IDeviceGrain>(item.DeviceId).RecoverPendingCommandAsync(
                    new DeviceCommandEnqueueRecovery
                    {
                        MsgId = item.MsgId,
                        DeviceSequence = item.DeviceSequence,
                        DueAt = item.DueAt
                    });
            }

            state.State.Initialized = true;
            state.State.LastRecoveryAt = now;
            state.State.NextCleanupAt ??= GetInitialCleanupAt(now, shardId);
            await state.WriteStateAsync();
            if (state.State.NextCleanupAt <= now)
                await TryCleanupCommandsAsync(now, shardId);
            DeviceCommandRuntimeMetrics.RecordRecovery(
                shardId,
                Stopwatch.GetElapsedTime(startedAt),
                _latestByDevice.Count);
            ArmTimer();
        }
        catch (Exception exception)
        {
            LogRecoveryFailed(logger, exception, shardId);
            throw;
        }
    }

    /// <summary>
    /// 在当前分片清理时间到达后执行有界扫描；清理失败不会阻断设备调度恢复，下一次 Reminder 会重试。
    /// </summary>
    /// <param name="now">本轮恢复使用的统一时间。</param>
    /// <param name="shardId">当前固定分片编号。</param>
    private async Task TryCleanupCommandsAsync(DateTimeOffset now, int shardId)
    {
        try
        {
            await CleanupCommandsAsync(now, shardId);
        }
        catch (Exception exception)
        {
            LogCleanupFailed(logger, exception, shardId);
            DeviceCommandRuntimeMetrics.RecordCleanupFailure("scan");
        }
    }

    /// <summary>
    /// 先物理删除满总保留期的状态，再压缩只超过热数据期限的 Payload，并持久化下一次清理水位。
    /// </summary>
    /// <param name="now">本轮清理统一时间。</param>
    /// <param name="shardId">当前固定分片编号。</param>
    private async Task CleanupCommandsAsync(DateTimeOffset now, int shardId)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var deleteThrough = now.AddDays(-_options.CommandRetentionDays);
        var compactThrough = now.AddDays(-_options.PayloadRetentionDays);
        var purged = await PurgeCommandBatchesAsync(shardId, deleteThrough);
        var compacted = await CompactPayloadBatchesAsync(shardId, compactThrough, deleteThrough);

        state.State.PurgedCommandCount += purged;
        state.State.CompactedCommandCount += compacted;
        state.State.LastCleanupAt = now;
        state.State.NextCleanupAt = now.AddMinutes(_options.CleanupScanIntervalMinutes);
        await state.WriteStateAsync();
        DeviceCommandRuntimeMetrics.RecordCleanup(
            shardId,
            Stopwatch.GetElapsedTime(startedAt),
            compacted,
            purged);
    }

    /// <summary>
    /// 分批读取并物理删除达到总保留期限的 CommandGrain 状态。
    /// </summary>
    /// <param name="shardId">当前固定分片编号。</param>
    /// <param name="deleteThrough">允许删除的最晚终态时间。</param>
    /// <returns>本轮成功删除的命令数量。</returns>
    private async Task<int> PurgeCommandBatchesAsync(int shardId, DateTimeOffset deleteThrough)
    {
        var total = 0;
        for (var batch = 0; batch < _options.CleanupMaxBatchesPerRun; batch++)
        {
            var candidates = await schedulingStore.ReadCommandPurgeCandidatesAsync(
                shardId,
                deleteThrough,
                _options.CleanupBatchSize);
            if (candidates.Count == 0)
                break;

            total += await ProcessCleanupCandidatesAsync(
                candidates,
                "purge",
                msgId => GrainFactory.GetGrain<IDeviceCommandGrain>(msgId).PurgeAsync(deleteThrough));
            if (candidates.Count < _options.CleanupBatchSize)
                break;
        }

        return total;
    }

    /// <summary>
    /// 分批清空超过热数据期限但尚未达到删除期限的终态命令 Payload。
    /// </summary>
    /// <param name="shardId">当前固定分片编号。</param>
    /// <param name="compactThrough">允许压缩的最晚终态时间。</param>
    /// <param name="deleteThrough">允许直接删除的最晚终态时间。</param>
    /// <returns>本轮成功压缩的命令数量。</returns>
    private async Task<int> CompactPayloadBatchesAsync(
        int shardId,
        DateTimeOffset compactThrough,
        DateTimeOffset deleteThrough)
    {
        var total = 0;
        for (var batch = 0; batch < _options.CleanupMaxBatchesPerRun; batch++)
        {
            var candidates = await schedulingStore.ReadPayloadCompactionCandidatesAsync(
                shardId,
                compactThrough,
                deleteThrough,
                _options.CleanupBatchSize);
            if (candidates.Count == 0)
                break;

            total += await ProcessCleanupCandidatesAsync(
                candidates,
                "compact",
                msgId => GrainFactory.GetGrain<IDeviceCommandGrain>(msgId).CompactPayloadAsync(compactThrough));
            if (candidates.Count < _options.CleanupBatchSize)
                break;
        }

        return total;
    }

    /// <summary>
    /// 以固定 16 路并发处理一批 Grain 候选；单条失败会记录后跳过，不阻断同批其他命令。
    /// </summary>
    /// <param name="candidates">本批候选 MsgId。</param>
    /// <param name="operation">有限操作名：compact 或 purge。</param>
    /// <param name="action">对单条 CommandGrain 执行复核和维护的异步操作。</param>
    /// <returns>实际改变状态的命令数量。</returns>
    private async Task<int> ProcessCleanupCandidatesAsync(
        IReadOnlyList<string> candidates,
        string operation,
        Func<string, Task<bool>> action)
    {
        var completed = 0;
        foreach (var chunk in candidates.Chunk(CleanupConcurrency))
        {
            var results = await Task.WhenAll(chunk.Select(msgId => TryProcessCleanupCandidateAsync(msgId, operation, action)));
            completed += results.Count(changed => changed);
        }

        return completed;
    }

    /// <summary>
    /// 隔离单条清理候选异常，防止损坏记录长期阻塞同一 SQL 批次中的其他命令。
    /// </summary>
    /// <param name="msgId">候选命令编号。</param>
    /// <param name="operation">有限操作名：compact 或 purge。</param>
    /// <param name="action">待执行的 Grain 维护操作。</param>
    /// <returns>候选实际被压缩或删除时返回 true。</returns>
    private async Task<bool> TryProcessCleanupCandidateAsync(
        string msgId,
        string operation,
        Func<string, Task<bool>> action)
    {
        try
        {
            return await action(msgId);
        }
        catch (Exception exception)
        {
            LogCleanupCandidateFailed(logger, exception, operation, msgId, GetShardId());
            DeviceCommandRuntimeMetrics.RecordCleanupFailure(operation);
            return false;
        }
    }

    /// <summary>
    /// 计算首次清理时间，按分片编号分散到一个清理周期内，避免 1024 个分片启动时同时扫描 SQL。
    /// </summary>
    /// <param name="now">首次恢复时间。</param>
    /// <param name="shardId">当前固定分片编号。</param>
    /// <returns>首次允许清理的绝对时间。</returns>
    private DateTimeOffset GetInitialCleanupAt(DateTimeOffset now, int shardId)
    {
        var minuteOffset = shardId % _options.CleanupScanIntervalMinutes;
        var secondOffset = (shardId / _options.CleanupScanIntervalMinutes) % 60;
        return now.AddMinutes(1 + minuteOffset).AddSeconds(secondOffset);
    }

    /// <summary>
    /// 执行最小堆中已到期的有限批次，并通过单向 Grain 调用唤醒设备。
    /// </summary>
    /// <param name="cancellationToken">Timer 取消信号。</param>
    private async Task OnTimerAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var limit = checked(_options.BatchSize * _options.MaxBatchesPerTick);
        var sent = 0;
        while (sent < limit && _queue.TryPeek(out var entry, out var dueAt) && dueAt <= now)
        {
            _queue.Dequeue();
            if (!_latestByDevice.TryGetValue(entry.DeviceId, out var latest) ||
                latest.ScheduleVersion != entry.ScheduleVersion ||
                latest.DueAt != entry.DueAt)
            {
                state.State.StaleScheduleCount++;
                DeviceCommandRuntimeMetrics.RecordStaleSchedule(GetShardId());
                continue;
            }

            _latestByDevice.Remove(entry.DeviceId);
            await GrainFactory.GetGrain<IDeviceGrain>(entry.DeviceId).WakeScheduledCommandAsync(
                new DeviceCommandWakeup
                {
                    ScheduleVersion = entry.ScheduleVersion,
                    DueAt = entry.DueAt
                });
            sent++;
            state.State.WakeupCount++;
            DeviceCommandRuntimeMetrics.RecordSchedulerWakeup(GetShardId(), entry.DueAt, now);
        }

        if (sent == limit && _queue.TryPeek(out _, out var remainingDueAt) && remainingDueAt <= now)
            _timer?.Change(TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);
        else
            ArmTimer();
    }

    /// <summary>
    /// 保留每台设备的最新调度版本；旧堆项通过出队时校验惰性清理。
    /// </summary>
    /// <param name="deviceId">设备编号。</param>
    /// <param name="scheduleVersion">设备调度版本。</param>
    /// <param name="dueAt">下一动作时间。</param>
    private void AddOrUpdate(string deviceId, long scheduleVersion, DateTimeOffset dueAt)
    {
        if (_latestByDevice.TryGetValue(deviceId, out var current) && current.ScheduleVersion >= scheduleVersion)
        {
            state.State.StaleScheduleCount++;
            DeviceCommandRuntimeMetrics.RecordStaleSchedule(GetShardId());
            return;
        }

        var entry = new ScheduledEntry(deviceId, scheduleVersion, dueAt);
        _latestByDevice[deviceId] = entry;
        _queue.Enqueue(entry, dueAt);
    }

    /// <summary>
    /// 把分片唯一 Timer 调整到当前最早任务；没有任务时停止 Timer。
    /// </summary>
    private void ArmTimer()
    {
        if (_timer is null)
            return;

        while (_queue.TryPeek(out var entry, out _))
        {
            if (_latestByDevice.TryGetValue(entry.DeviceId, out var latest) &&
                latest.ScheduleVersion == entry.ScheduleVersion &&
                latest.DueAt == entry.DueAt)
            {
                var dueTime = entry.DueAt - DateTimeOffset.UtcNow;
                if (dueTime < TimeSpan.Zero)
                    dueTime = TimeSpan.Zero;
                _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
                return;
            }

            _queue.Dequeue();
            state.State.StaleScheduleCount++;
            DeviceCommandRuntimeMetrics.RecordStaleSchedule(GetShardId());
        }

        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 读取并校验当前 Grain 的长整型分片主键。
    /// </summary>
    /// <returns>非负且小于配置分片数的编号。</returns>
    /// <exception cref="InvalidOperationException">Grain 主键超出配置范围。</exception>
    private int GetShardId()
    {
        var key = this.GetPrimaryKeyLong();
        if (key < 0 || key >= _options.ShardCount)
            throw new InvalidOperationException($"调度分片 {key} 超出配置范围 0..{_options.ShardCount - 1}。");
        return (int)key;
    }

    /// <summary>
    /// 创建不暴露具体设备编号的分片运行快照。
    /// </summary>
    /// <returns>当前调度统计。</returns>
    private DeviceCommandSchedulerSnapshot CreateSnapshot() => new()
    {
        ShardId = GetShardId(),
        ScheduledDeviceCount = _latestByDevice.Count,
        LastRecoveryAt = state.State.LastRecoveryAt,
        StaleScheduleCount = state.State.StaleScheduleCount,
        WakeupCount = state.State.WakeupCount,
        LastCleanupAt = state.State.LastCleanupAt,
        NextCleanupAt = state.State.NextCleanupAt,
        CompactedCommandCount = state.State.CompactedCommandCount,
        PurgedCommandCount = state.State.PurgedCommandCount
    };

    [LoggerMessage(2101, LogLevel.Error, "命令调度分片持久化恢复失败: {ShardId}")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception, int shardId);

    [LoggerMessage(2102, LogLevel.Error, "命令历史清理分片扫描失败: {ShardId}")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception, int shardId);

    [LoggerMessage(2103, LogLevel.Warning, "单条命令历史清理失败: {Operation} {MsgId} {ShardId}")]
    private static partial void LogCleanupCandidateFailed(
        ILogger logger,
        Exception exception,
        string operation,
        string msgId,
        int shardId);

    /// <summary>
    /// 内存最小堆中的不可变设备调度项。
    /// </summary>
    /// <param name="DeviceId">设备编号。</param>
    /// <param name="ScheduleVersion">设备调度版本。</param>
    /// <param name="DueAt">下一动作时间。</param>
    private sealed record ScheduledEntry(string DeviceId, long ScheduleVersion, DateTimeOffset DueAt);
}
