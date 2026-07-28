using Orleans;

namespace DeviceSocket.Cluster.Contracts.Gateways;

/// <summary>
/// Orleans 中保存的网关节点快照。
/// </summary>
[GenerateSerializer]
public sealed class GatewayNodeSnapshot
{
    /// <summary>稳定的网关节点编号。</summary>
    [Id(0)] public string GatewayNodeId { get; set; } = string.Empty;

    /// <summary>当前进程实例编号。</summary>
    [Id(1)] public string InstanceId { get; set; } = string.Empty;

    /// <summary>MagicOnion HTTP/2 服务地址。</summary>
    [Id(2)] public string RpcEndpoint { get; set; } = string.Empty;

    /// <summary>当前支持的协议名称。</summary>
    [Id(3)] public string[] SupportedProtocols { get; set; } = [];

    /// <summary>进程启动时间。</summary>
    [Id(4)] public DateTimeOffset StartedAt { get; set; }

    /// <summary>最近一次续租时间。</summary>
    [Id(5)] public DateTimeOffset LastHeartbeatAt { get; set; }

    /// <summary>节点租约到期时间。</summary>
    [Id(6)] public DateTimeOffset LeaseExpiresAt { get; set; }
}
