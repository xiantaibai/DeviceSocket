namespace DeviceSocket.Host.Networking.Cluster;

/// <summary>
/// 当前通信网关在 Orleans 和 MagicOnion 内部网络中的身份与租约配置。
/// </summary>
public sealed class GatewayNodeOptions
{
    /// <summary>配置文件节名称。</summary>
    public const string SectionName = "Gateway";

    /// <summary>稳定的网关节点编号。</summary>
    public string NodeId { get; set; } = "gateway-local-01";

    /// <summary>MagicOnion HTTP/2 监听端口。</summary>
    public int RpcPort { get; set; } = 7001;

    /// <summary>其他服务可以直接访问的 MagicOnion 地址。</summary>
    public string RpcEndpoint { get; set; } = "http://127.0.0.1:7001";

    /// <summary>设备路由租约秒数。</summary>
    public int RouteLeaseSeconds { get; set; } = 60;

    /// <summary>网关节点租约秒数。</summary>
    public int NodeLeaseSeconds { get; set; } = 30;

    /// <summary>网关节点续租周期秒数。</summary>
    public int NodeHeartbeatSeconds { get; set; } = 10;
}
