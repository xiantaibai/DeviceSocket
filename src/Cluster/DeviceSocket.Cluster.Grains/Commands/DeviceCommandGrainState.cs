using DeviceSocket.Cluster.Contracts.Commands;
using Orleans;

namespace DeviceSocket.Cluster.Grains.Commands;

/// <summary>
/// 单条可靠消息的持久化状态；存储提供程序可从内存替换为 Redis 或数据库。
/// </summary>
[GenerateSerializer]
public sealed class DeviceCommandGrainState
{
    /// <summary>是否已经成功创建过该 MsgId 对应的消息。</summary>
    [Id(0)] public bool Initialized { get; set; }

    /// <summary>可靠消息当前完整状态。</summary>
    [Id(1)] public ReliableCommandSnapshot Snapshot { get; set; } = new();
}
