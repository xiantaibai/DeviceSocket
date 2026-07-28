using DeviceSocket.Cluster.Contracts.Gateways;
using Orleans;

namespace DeviceSocket.Cluster.Grains.Gateways;

/// <summary>
/// 网关节点 Grain 的持久化状态。
/// </summary>
[GenerateSerializer]
public sealed class GatewayNodeState
{
    /// <summary>最近一次成功注册的节点快照。</summary>
    [Id(0)] public GatewayNodeSnapshot Snapshot { get; set; } = new();
}
