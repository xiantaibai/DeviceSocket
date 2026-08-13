using Orleans;

namespace DeviceSocket.Cluster.Contracts.Scheduling;

/// <summary>
/// 命令调度分片的轻量运行快照。
/// </summary>
[GenerateSerializer]
public sealed class DeviceCommandSchedulerSnapshot
{
    /// <summary>调度分片编号。</summary>
    [Id(0)] public int ShardId { get; set; }

    /// <summary>当前内存中保留的最新设备调度项数量。</summary>
    [Id(1)] public int ScheduledDeviceCount { get; set; }

    /// <summary>最近一次完成持久化恢复扫描的时间。</summary>
    [Id(2)] public DateTimeOffset? LastRecoveryAt { get; set; }

    /// <summary>累计丢弃的旧版本通知或唤醒数量。</summary>
    [Id(3)] public long StaleScheduleCount { get; set; }

    /// <summary>累计向 DeviceGrain 发出的到期唤醒数量。</summary>
    [Id(4)] public long WakeupCount { get; set; }

    /// <summary>最近一次完成终态命令清理扫描的时间。</summary>
    [Id(5)] public DateTimeOffset? LastCleanupAt { get; set; }

    /// <summary>当前分片下一次允许执行终态命令清理的时间。</summary>
    [Id(6)] public DateTimeOffset? NextCleanupAt { get; set; }

    /// <summary>累计完成 Payload 压缩的命令数量。</summary>
    [Id(7)] public long CompactedCommandCount { get; set; }

    /// <summary>累计物理删除的 CommandGrain 状态数量。</summary>
    [Id(8)] public long PurgedCommandCount { get; set; }
}
