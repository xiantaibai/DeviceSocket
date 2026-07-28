using Orleans;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// 网关在设备登录后提交给 Orleans 的连接路由信息。
/// </summary>
[GenerateSerializer]
public sealed class DeviceRouteRegistration
{
    /// <summary>持有真实 Socket 的网关节点编号。</summary>
    [Id(0)] public string GatewayNodeId { get; set; } = string.Empty;

    /// <summary>Cluster Silo 可访问的 MagicOnion HTTP/2 地址。</summary>
    [Id(1)] public string GatewayRpcEndpoint { get; set; } = string.Empty;

    /// <summary>当前 SuperSocket Session 编号。</summary>
    [Id(2)] public string SessionId { get; set; } = string.Empty;

    /// <summary>设备协议名称。</summary>
    [Id(3)] public string Protocol { get; set; } = string.Empty;

    /// <summary>设备远端地址，仅用于诊断。</summary>
    [Id(4)] public string? RemoteEndPoint { get; set; }

    /// <summary>本节点接受连接的时间。</summary>
    [Id(5)] public DateTimeOffset ConnectedAt { get; set; }

    /// <summary>路由租约到期时间，过期后下发端不得继续使用。</summary>
    [Id(6)] public DateTimeOffset LeaseExpiresAt { get; set; }
}
