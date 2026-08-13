using Orleans;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// 分片调度器发给 DeviceGrain 的版本化唤醒消息。
/// </summary>
[GenerateSerializer]
public sealed class DeviceCommandWakeup
{
    /// <summary>调度器观察到的设备调度版本。</summary>
    [Id(0)] public long ScheduleVersion { get; set; }

    /// <summary>该版本计划执行的时间。</summary>
    [Id(1)] public DateTimeOffset DueAt { get; set; }
}
