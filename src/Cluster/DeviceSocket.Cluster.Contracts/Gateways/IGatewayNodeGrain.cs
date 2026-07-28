using Orleans;

namespace DeviceSocket.Cluster.Contracts.Gateways;

/// <summary>
/// 按 GatewayNodeId 激活的网关节点 Grain，用于内部服务发现和租约判断。
/// </summary>
public interface IGatewayNodeGrain : IGrainWithStringKey
{
    /// <summary>注册新实例或使用相同实例编号续租。</summary>
    Task<GatewayNodeSnapshot> RegisterOrRenewAsync(GatewayNodeRegistration registration);

    /// <summary>查询节点信息；调用方必须自行检查 LeaseExpiresAt。</summary>
    Task<GatewayNodeSnapshot> GetSnapshotAsync();
}
