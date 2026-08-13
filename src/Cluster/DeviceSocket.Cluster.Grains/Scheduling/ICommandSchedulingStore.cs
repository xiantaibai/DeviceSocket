namespace DeviceSocket.Cluster.Grains.Scheduling;

using DeviceSocket.Cluster.Contracts.Commands;

/// <summary>
/// Grain 业务逻辑所需的命令索引查询能力；SQL Server 与未来其他数据库分别实现该抽象。
/// </summary>
public interface ICommandSchedulingStore
{
    /// <summary>
    /// 通知存储实现命令状态已经持久化；SQL 实现的索引与 Grain 状态同表原子更新，因此该调用为空操作，测试内存实现据此维护索引。
    /// </summary>
    /// <param name="snapshot">已经完成持久化的命令快照。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>索引同步任务。</returns>
    Task CommandStateChangedAsync(
        ReliableCommandSnapshot snapshot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 通知存储实现 CommandGrain 状态已被物理删除；SQL 实现与状态表共用记录无需二次操作，测试或外置索引实现可据此移除副本。
    /// </summary>
    /// <param name="msgId">已删除的全局消息编号。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>索引删除任务。</returns>
    Task CommandStateDeletedAsync(
        string msgId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询设备下一条命令；优先返回已经到期的最高优先级命令，否则返回最早的未来命令。
    /// </summary>
    /// <param name="deviceId">目标设备编号。</param>
    /// <param name="now">本次选择使用的统一时间。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>下一命令索引；没有等待命令时返回空。</returns>
    Task<DeviceCommandQueueEntry?> FindNextCommandAsync(
        string deviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 扫描指定分片在时间窗口内需要唤醒的设备，按到期时间升序返回。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="dueThrough">扫描窗口截止时间。</param>
    /// <param name="take">最大返回数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>版本化设备唤醒项。</returns>
    Task<IReadOnlyList<PersistedDeviceSchedule>> ReadDueDevicesAsync(
        int shardId,
        DateTimeOffset dueThrough,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 扫描创建后长时间未被 DeviceGrain 接管的命令，供调度分片幂等修复。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="recoveryAt">恢复扫描时间。</param>
    /// <param name="take">最大返回数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>待修复命令索引。</returns>
    Task<IReadOnlyList<PendingCommandRecovery>> ReadPendingEnqueuesAsync(
        int shardId,
        DateTimeOffset recoveryAt,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 扫描已达到总保留期限的终态命令，只返回轻量 MsgId，最终删除资格由 CommandGrain 再次校验。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="terminalThrough">允许删除的最晚终态时间。</param>
    /// <param name="take">最大返回数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>按终态时间升序排列的删除候选 MsgId。</returns>
    Task<IReadOnlyList<string>> ReadCommandPurgeCandidatesAsync(
        int shardId,
        DateTimeOffset terminalThrough,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 扫描已达到热数据期限但尚未达到删除期限的终态命令，只返回需要压缩 Payload 的 MsgId。
    /// </summary>
    /// <param name="shardId">调度分片编号。</param>
    /// <param name="compactThrough">允许压缩的最晚终态时间。</param>
    /// <param name="deleteThrough">已经应该直接删除的最晚终态时间，用于避免同轮重复处理。</param>
    /// <param name="take">最大返回数量。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>按终态时间升序排列的 Payload 压缩候选 MsgId。</returns>
    Task<IReadOnlyList<string>> ReadPayloadCompactionCandidatesAsync(
        int shardId,
        DateTimeOffset compactThrough,
        DateTimeOffset deleteThrough,
        int take,
        CancellationToken cancellationToken = default);
}
