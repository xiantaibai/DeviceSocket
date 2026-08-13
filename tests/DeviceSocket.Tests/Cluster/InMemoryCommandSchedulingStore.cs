using System.Collections.Concurrent;
using DeviceSocket.Cluster.Contracts.Commands;
using DeviceSocket.Cluster.Grains.Scheduling;

namespace DeviceSocket.Tests.Cluster;

/// <summary>
/// 测试集群使用的轻量命令索引，以关系表相同的状态、优先级和设备顺序规则选择队首。
/// </summary>
public sealed class InMemoryCommandSchedulingStore : ICommandSchedulingStore
{
    private static readonly ConcurrentDictionary<string, ReliableCommandSnapshot> Commands =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 在 CommandGrain 状态持久化后更新内存索引，并深复制 Payload 防止共享可变状态。
    /// </summary>
    /// <param name="snapshot">最新命令快照。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>已经完成的同步任务。</returns>
    public Task CommandStateChangedAsync(
        ReliableCommandSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        Commands[snapshot.MsgId] = Copy(snapshot);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 移除已被 CommandGrain 物理删除的内存索引副本。
    /// </summary>
    /// <param name="msgId">已删除的消息编号。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>已经完成的同步任务。</returns>
    public Task CommandStateDeletedAsync(
        string msgId,
        CancellationToken cancellationToken = default)
    {
        Commands.TryRemove(msgId, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 按生产 SQL 相同规则选择已到期最高优先级命令，否则选择最早未来命令。
    /// </summary>
    /// <param name="deviceId">目标设备编号。</param>
    /// <param name="now">选择时间。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>队首命令索引或空。</returns>
    public Task<DeviceCommandQueueEntry?> FindNextCommandAsync(
        string deviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var candidates = Commands.Values
            .Where(command =>
                string.Equals(command.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
                command.Status == ReliableMessageStatus.Queued &&
                command.ExpiresAt > now)
            .ToArray();
        var selected = candidates
            .Where(command => command.NotBefore <= now)
            .OrderByDescending(command => command.Priority)
            .ThenBy(command => command.DeviceSequence)
            .FirstOrDefault() ?? candidates
            .Where(command => command.NotBefore > now)
            .OrderBy(command => command.NotBefore)
            .ThenByDescending(command => command.Priority)
            .ThenBy(command => command.DeviceSequence)
            .FirstOrDefault();

        return Task.FromResult(selected is null
            ? null
            : new DeviceCommandQueueEntry
            {
                MsgId = selected.MsgId,
                DueAt = selected.NotBefore,
                Priority = selected.Priority,
                DeviceSequence = selected.DeviceSequence
            });
    }

    /// <summary>
    /// 测试中的设备调度由 DeviceGrain 直接通知 SchedulerGrain，因此无需模拟 DeviceState 表扫描。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="dueThrough">扫描截止时间。</param>
    /// <param name="take">最大数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>空设备恢复列表。</returns>
    public Task<IReadOnlyList<PersistedDeviceSchedule>> ReadDueDevicesAsync(
        int shardId,
        DateTimeOffset dueThrough,
        int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PersistedDeviceSchedule>>([]);

    /// <summary>
    /// 返回达到恢复时间的 EnqueuePending 命令，用于验证跨 Grain 中断修复入口。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="recoveryAt">恢复扫描时间。</param>
    /// <param name="take">最大数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>待修复命令索引。</returns>
    public Task<IReadOnlyList<PendingCommandRecovery>> ReadPendingEnqueuesAsync(
        int shardId,
        DateTimeOffset recoveryAt,
        int take,
        CancellationToken cancellationToken = default)
    {
        var result = Commands.Values
            .Where(command =>
                command.SchedulerShard == shardId &&
                command.Status == ReliableMessageStatus.EnqueuePending &&
                command.EnqueueRecoveryAt <= recoveryAt)
            .OrderBy(command => command.EnqueueRecoveryAt)
            .Take(take)
            .Select(command => new PendingCommandRecovery
            {
                DeviceId = command.DeviceId,
                MsgId = command.MsgId,
                DeviceSequence = command.DeviceSequence,
                DueAt = command.NotBefore
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<PendingCommandRecovery>>(result);
    }

    /// <summary>
    /// 返回达到总保留期限的终态命令 MsgId，模拟生产 SQL 的分片与时间排序。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="terminalThrough">允许删除的最晚终态时间。</param>
    /// <param name="take">最大返回数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>删除候选 MsgId。</returns>
    public Task<IReadOnlyList<string>> ReadCommandPurgeCandidatesAsync(
        int shardId,
        DateTimeOffset terminalThrough,
        int take,
        CancellationToken cancellationToken = default)
    {
        var result = Commands.Values
            .Where(command =>
                command.SchedulerShard == shardId &&
                command.TerminalAt is { } terminalAt &&
                terminalAt <= terminalThrough)
            .OrderBy(command => command.TerminalAt)
            .ThenBy(command => command.MsgId, StringComparer.Ordinal)
            .Take(take)
            .Select(command => command.MsgId)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    /// <summary>
    /// 返回达到热数据期限、尚未到删除期限且未压缩 Payload 的终态命令 MsgId。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="compactThrough">允许压缩的最晚终态时间。</param>
    /// <param name="deleteThrough">允许直接删除的最晚终态时间。</param>
    /// <param name="take">最大返回数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>Payload 压缩候选 MsgId。</returns>
    public Task<IReadOnlyList<string>> ReadPayloadCompactionCandidatesAsync(
        int shardId,
        DateTimeOffset compactThrough,
        DateTimeOffset deleteThrough,
        int take,
        CancellationToken cancellationToken = default)
    {
        var result = Commands.Values
            .Where(command =>
                command.SchedulerShard == shardId &&
                command.PayloadCompactedAt is null &&
                command.TerminalAt is { } terminalAt &&
                terminalAt > deleteThrough &&
                terminalAt <= compactThrough)
            .OrderBy(command => command.TerminalAt)
            .ThenBy(command => command.MsgId, StringComparer.Ordinal)
            .Take(take)
            .Select(command => command.MsgId)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    /// <summary>
    /// 深复制完整命令快照，测试索引只读副本不影响 Grain 状态。
    /// </summary>
    /// <param name="source">源快照。</param>
    /// <returns>独立快照。</returns>
    private static ReliableCommandSnapshot Copy(ReliableCommandSnapshot source) => new()
    {
        MsgId = source.MsgId,
        DeviceId = source.DeviceId,
        Protocol = source.Protocol,
        Payload = source.Payload.ToArray(),
        PayloadHash = source.PayloadHash,
        TraceId = source.TraceId,
        Status = source.Status,
        AttemptNo = source.AttemptNo,
        Message = source.Message,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        ExpiresAt = source.ExpiresAt,
        NextRetryAt = source.NextRetryAt,
        RequireDeviceConfirmation = source.RequireDeviceConfirmation,
        NotBefore = source.NotBefore,
        ReplyDeadline = source.ReplyDeadline,
        ResponseTimeout = source.ResponseTimeout,
        MaxAttempts = source.MaxAttempts,
        Priority = source.Priority,
        DeviceSequence = source.DeviceSequence,
        ActionVersion = source.ActionVersion,
        SchedulerShard = source.SchedulerShard,
        ResponseMatcher = source.ResponseMatcher,
        EnqueueRecoveryAt = source.EnqueueRecoveryAt,
        ActiveSessionEpoch = source.ActiveSessionEpoch,
        TerminalAt = source.TerminalAt,
        PayloadCompactedAt = source.PayloadCompactedAt
    };
}
