namespace DeviceSocket.Cluster.Silo.Configuration;

/// <summary>
/// Orleans 集群身份和监听端点配置，对应配置节 <c>Orleans:Cluster</c>。
/// </summary>
public sealed class OrleansClusterRuntimeOptions
{
    /// <summary>配置节路径。</summary>
    public const string SectionName = "Orleans:Cluster";

    /// <summary>同一环境内集群编号，默认 <c>devicesocket-prod</c>；不同环境必须使用不同值。</summary>
    public string ClusterId { get; set; } = "devicesocket-prod";

    /// <summary>共享 Grain 数据和接口的服务编号，默认 <c>devicesocket</c>。</summary>
    public string ServiceId { get; set; } = "devicesocket";

    /// <summary>Silo 间通信端口，默认 11111；零值非法。</summary>
    public int SiloPort { get; set; } = 11111;

    /// <summary>Orleans Client 连接端口，默认 30000；零值非法。</summary>
    public int GatewayPort { get; set; } = 30000;

    /// <summary>向集群成员表发布的 IP 地址，默认回环地址；容器或多机部署必须覆盖为节点可达地址。</summary>
    public string AdvertisedIPAddress { get; set; } = "127.0.0.1";
}
