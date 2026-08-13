using System.Diagnostics.Metrics;
using DeviceSocket.Cluster.Contracts.Commands;

namespace DeviceSocket.Cluster.Grains.Scheduling;

/// <summary>
/// 记录百万设备命令闭环的低基数运行指标，不使用 DeviceId 或 MsgId 作为标签。
/// </summary>
public static class DeviceCommandRuntimeMetrics
{
    private static readonly Meter Meter = new("DeviceSocket.Cluster.Commands", "1.0.0");
    private static readonly Counter<long> AcceptedCommands =
        Meter.CreateCounter<long>("devicesocket.command.accepted", description: "已持久化受理的可靠命令数。");
    private static readonly Counter<long> CommandStates =
        Meter.CreateCounter<long>("devicesocket.command.state.transitions", description: "可靠命令状态持久化次数。");
    private static readonly Counter<long> SchedulerWakeups =
        Meter.CreateCounter<long>("devicesocket.command.scheduler.wakeups", description: "分片调度器发出的设备唤醒数。");
    private static readonly Counter<long> StaleSchedules =
        Meter.CreateCounter<long>("devicesocket.command.scheduler.stale", description: "被丢弃的旧版本调度项数。");
    private static readonly Histogram<double> SchedulerLag =
        Meter.CreateHistogram<double>("devicesocket.command.scheduler.lag", "ms", "设备动作到期到实际唤醒的延迟。");
    private static readonly Histogram<long> SchedulerDepth =
        Meter.CreateHistogram<long>("devicesocket.command.scheduler.depth", description: "调度分片内存中的最新设备任务数。");
    private static readonly Histogram<double> RecoveryDuration =
        Meter.CreateHistogram<double>("devicesocket.command.scheduler.recovery.duration", "ms", "分片 SQL 恢复扫描耗时。");
    private static readonly Counter<long> RouteWrites =
        Meter.CreateCounter<long>("devicesocket.device.route.writes", description: "设备路由持久化写入次数。");
    private static readonly Histogram<double> SqlQueryDuration =
        Meter.CreateHistogram<double>("devicesocket.command.sql.duration", "ms", "命令索引 SQL 查询耗时。");
    private static readonly Histogram<long> SqlRows =
        Meter.CreateHistogram<long>("devicesocket.command.sql.rows", description: "命令索引 SQL 查询返回行数。");
    private static readonly Counter<long> CompactedPayloads =
        Meter.CreateCounter<long>("devicesocket.command.cleanup.compacted", description: "达到热数据期限后完成 Payload 压缩的命令数。");
    private static readonly Counter<long> PurgedCommands =
        Meter.CreateCounter<long>("devicesocket.command.cleanup.purged", description: "达到总保留期限后物理删除的 CommandGrain 状态数。");
    private static readonly Histogram<double> CleanupDuration =
        Meter.CreateHistogram<double>("devicesocket.command.cleanup.duration", "ms", "调度分片执行一轮历史清理的耗时。");
    private static readonly Histogram<long> CleanupRows =
        Meter.CreateHistogram<long>("devicesocket.command.cleanup.rows", description: "调度分片单轮压缩或删除的命令数。");
    private static readonly Counter<long> CleanupFailures =
        Meter.CreateCounter<long>("devicesocket.command.cleanup.failures", description: "历史清理候选处理失败次数。");

    /// <summary>
    /// 记录一条可靠命令首次受理。
    /// </summary>
    public static void RecordAcceptedCommand() => AcceptedCommands.Add(1);

    /// <summary>
    /// 记录一次命令状态持久化，标签只使用有限枚举值。
    /// </summary>
    /// <param name="status">持久化后的生命周期状态。</param>
    public static void RecordCommandState(ReliableMessageStatus status) =>
        CommandStates.Add(1, new KeyValuePair<string, object?>("status", status.ToString()));

    /// <summary>
    /// 记录调度唤醒及其相对 DueAt 的延迟。
    /// </summary>
    /// <param name="shardId">固定调度分片编号。</param>
    /// <param name="dueAt">计划动作时间。</param>
    /// <param name="wokeAt">实际唤醒时间。</param>
    public static void RecordSchedulerWakeup(int shardId, DateTimeOffset dueAt, DateTimeOffset wokeAt)
    {
        var shard = new KeyValuePair<string, object?>("shard", shardId);
        SchedulerWakeups.Add(1, shard);
        SchedulerLag.Record(Math.Max(0, (wokeAt - dueAt).TotalMilliseconds), shard);
    }

    /// <summary>
    /// 记录被版本校验丢弃的调度通知或堆项。
    /// </summary>
    /// <param name="shardId">固定调度分片编号。</param>
    public static void RecordStaleSchedule(int shardId) =>
        StaleSchedules.Add(1, new KeyValuePair<string, object?>("shard", shardId));

    /// <summary>
    /// 记录一次分片恢复完成后的耗时和内存任务数量。
    /// </summary>
    /// <param name="shardId">固定调度分片编号。</param>
    /// <param name="elapsed">恢复耗时。</param>
    /// <param name="scheduledCount">恢复后内存中的最新设备数。</param>
    public static void RecordRecovery(int shardId, TimeSpan elapsed, int scheduledCount)
    {
        var shard = new KeyValuePair<string, object?>("shard", shardId);
        RecoveryDuration.Record(elapsed.TotalMilliseconds, shard);
        SchedulerDepth.Record(scheduledCount, shard);
    }

    /// <summary>
    /// 记录设备路由因注册、断开或合并阈值到达而实际写入存储。
    /// </summary>
    /// <param name="reason">有限原因：register、touch 或 disconnect。</param>
    public static void RecordRouteWrite(string reason) =>
        RouteWrites.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>
    /// 记录结构化 SQL 索引查询耗时和返回行数。
    /// </summary>
    /// <param name="operation">有限查询名称。</param>
    /// <param name="elapsed">查询耗时。</param>
    /// <param name="rows">返回行数。</param>
    public static void RecordSqlQuery(string operation, TimeSpan elapsed, int rows)
    {
        var tag = new KeyValuePair<string, object?>("operation", operation);
        SqlQueryDuration.Record(elapsed.TotalMilliseconds, tag);
        SqlRows.Record(rows, tag);
    }

    /// <summary>
    /// 记录一条终态命令完成 Payload 压缩。
    /// </summary>
    public static void RecordPayloadCompacted() => CompactedPayloads.Add(1);

    /// <summary>
    /// 记录一条终态 CommandGrain 状态被物理删除。
    /// </summary>
    public static void RecordCommandPurged() => PurgedCommands.Add(1);

    /// <summary>
    /// 记录一个调度分片完成历史清理的耗时和处理数量。
    /// </summary>
    /// <param name="shardId">固定调度分片编号。</param>
    /// <param name="elapsed">本轮清理耗时。</param>
    /// <param name="compacted">完成 Payload 压缩的数量。</param>
    /// <param name="purged">完成物理删除的数量。</param>
    public static void RecordCleanup(int shardId, TimeSpan elapsed, int compacted, int purged)
    {
        var shard = new KeyValuePair<string, object?>("shard", shardId);
        CleanupDuration.Record(elapsed.TotalMilliseconds, shard);
        CleanupRows.Record(compacted, shard, new KeyValuePair<string, object?>("operation", "compact"));
        CleanupRows.Record(purged, shard, new KeyValuePair<string, object?>("operation", "purge"));
    }

    /// <summary>
    /// 记录单条清理候选处理失败，标签只使用有限操作类型。
    /// </summary>
    /// <param name="operation">有限操作名：compact 或 purge。</param>
    public static void RecordCleanupFailure(string operation) =>
        CleanupFailures.Add(1, new KeyValuePair<string, object?>("operation", operation));
}
