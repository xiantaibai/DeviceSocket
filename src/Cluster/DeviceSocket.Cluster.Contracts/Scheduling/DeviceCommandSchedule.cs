using Orleans;

namespace DeviceSocket.Cluster.Contracts.Scheduling;

/// <summary>
/// DeviceGrain 写入持久化状态后发送给分片调度器的下一动作通知。
/// </summary>
[GenerateSerializer]
public sealed class DeviceCommandSchedule
{
    /// <summary>需要唤醒的设备编号。</summary>
    [Id(0)] public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备状态中的调度版本。</summary>
    [Id(1)] public long ScheduleVersion { get; set; }

    /// <summary>设备下一次应取得执行权的时间。</summary>
    [Id(2)] public DateTimeOffset DueAt { get; set; }
}
