using Orleans;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// 分片恢复扫描交给 DeviceGrain 的未完成入队信息。
/// </summary>
[GenerateSerializer]
public sealed class DeviceCommandEnqueueRecovery
{
    /// <summary>需要确认接管的全局命令编号。</summary>
    [Id(0)] public string MsgId { get; set; } = string.Empty;

    /// <summary>命令已持久化的设备内顺序号。</summary>
    [Id(1)] public long DeviceSequence { get; set; }

    /// <summary>命令最早允许发送的时间。</summary>
    [Id(2)] public DateTimeOffset DueAt { get; set; }
}
