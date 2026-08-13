using DeviceSocket.Cluster.Contracts.Devices;
using Orleans;

namespace DeviceSocket.Cluster.Grains.Devices;

/// <summary>
/// 百万设备场景下的轻量设备状态，仅保存路由、当前执行权和下一调度游标，不保存完整命令队列。
/// </summary>
[GenerateSerializer]
public sealed class DeviceGrainState
{
    /// <summary>设备当前路由。</summary>
    [Id(0)] public DeviceRouteSnapshot Route { get; set; } = new();

    /// <summary>设备编号，作为关系型索引列的稳定来源。</summary>
    [Id(1)] public string DeviceId { get; set; } = string.Empty;

    /// <summary>当前占用设备一问一答执行通道的命令编号；为空表示通道空闲。</summary>
    [Id(2)] public string? ActiveCommandId { get; set; }

    /// <summary>是否存在当前命令或等待命令，供分片恢复扫描过滤。</summary>
    [Id(3)] public bool HasPendingWork { get; set; }

    /// <summary>当前设备下一次需要调度器唤醒的时间。</summary>
    [Id(4)] public DateTimeOffset? NextWakeAt { get; set; }

    /// <summary>每次改变下一动作时递增的版本，用于丢弃重复或迟到唤醒。</summary>
    [Id(5)] public long ScheduleVersion { get; set; }

    /// <summary>设备稳定映射到的调度分片编号。</summary>
    [Id(6)] public int SchedulerShard { get; set; }

    /// <summary>最后分配的设备内命令顺序号，仅用于排序，不代表业务状态。</summary>
    [Id(7)] public long NextDeviceSequence { get; set; }

    /// <summary>最近一次把路由心跳写入持久化存储的时间，用于抑制高频数据库更新。</summary>
    [Id(8)] public DateTimeOffset PersistedRouteAt { get; set; }
}
