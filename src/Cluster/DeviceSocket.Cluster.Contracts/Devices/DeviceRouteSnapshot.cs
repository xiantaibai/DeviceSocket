using Orleans;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// Orleans 对外提供的设备路由快照，不包含真实 Socket 对象。
/// </summary>
[GenerateSerializer]
public sealed class DeviceRouteSnapshot
{
    /// <summary>设备编号。</summary>
    [Id(0)] public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备当前是否已注册为在线。</summary>
    [Id(1)] public bool IsOnline { get; set; }

    /// <summary>持有连接的网关节点编号。</summary>
    [Id(2)] public string GatewayNodeId { get; set; } = string.Empty;

    /// <summary>目标网关的 MagicOnion 地址。</summary>
    [Id(3)] public string GatewayRpcEndpoint { get; set; } = string.Empty;

    /// <summary>当前 SuperSocket Session 编号。</summary>
    [Id(4)] public string SessionId { get; set; } = string.Empty;

    /// <summary>每次新 Session 登录都会递增的路由围栏值。</summary>
    [Id(5)] public long SessionEpoch { get; set; }

    /// <summary>设备协议名称。</summary>
    [Id(6)] public string Protocol { get; set; } = string.Empty;

    /// <summary>最近一次活动时间。</summary>
    [Id(7)] public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>当前路由租约到期时间。</summary>
    [Id(8)] public DateTimeOffset LeaseExpiresAt { get; set; }
}
