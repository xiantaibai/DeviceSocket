using Orleans;

namespace DeviceSocket.Cluster.Grains.Scheduling;

/// <summary>
/// 调度分片的少量持久化水位；内存时间堆丢失后从设备和命令状态表恢复。
/// </summary>
[GenerateSerializer]
public sealed class DeviceCommandSchedulerGrainState
{
    /// <summary>是否已完成首次初始化。</summary>
    [Id(0)] public bool Initialized { get; set; }

    /// <summary>最近一次完成持久化扫描的时间。</summary>
    [Id(1)] public DateTimeOffset? LastRecoveryAt { get; set; }

    /// <summary>累计丢弃的旧版本调度通知数量。</summary>
    [Id(2)] public long StaleScheduleCount { get; set; }

    /// <summary>累计发送的设备唤醒数量。</summary>
    [Id(3)] public long WakeupCount { get; set; }

    /// <summary>最近一次完成终态命令清理扫描的时间。</summary>
    [Id(4)] public DateTimeOffset? LastCleanupAt { get; set; }

    /// <summary>下一次允许执行终态命令清理的时间；首次初始化时按分片编号打散。</summary>
    [Id(5)] public DateTimeOffset? NextCleanupAt { get; set; }

    /// <summary>累计完成 Payload 压缩的命令数量。</summary>
    [Id(6)] public long CompactedCommandCount { get; set; }

    /// <summary>累计物理删除的 CommandGrain 状态数量。</summary>
    [Id(7)] public long PurgedCommandCount { get; set; }
}
