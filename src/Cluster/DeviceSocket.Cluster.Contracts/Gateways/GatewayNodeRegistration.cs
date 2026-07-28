using Orleans;

namespace DeviceSocket.Cluster.Contracts.Gateways;

/// <summary>
/// 网关节点启动和续租时提交的服务发现信息。
/// </summary>
[GenerateSerializer]
public sealed class GatewayNodeRegistration
{
    /// <summary>进程每次启动生成的实例编号。</summary>
    [Id(0)] public string InstanceId { get; set; } = string.Empty;

    /// <summary>MagicOnion HTTP/2 服务地址。</summary>
    [Id(1)] public string RpcEndpoint { get; set; } = string.Empty;

    /// <summary>当前节点支持的协议名称。</summary>
    [Id(2)] public string[] SupportedProtocols { get; set; } = [];

    /// <summary>本实例启动时间。</summary>
    [Id(3)] public DateTimeOffset StartedAt { get; set; }

    /// <summary>节点租约到期时间。</summary>
    [Id(4)] public DateTimeOffset LeaseExpiresAt { get; set; }
}
