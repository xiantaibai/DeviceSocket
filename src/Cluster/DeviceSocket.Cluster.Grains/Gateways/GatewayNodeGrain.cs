using DeviceSocket.Cluster.Contracts.Gateways;
using Orleans.Runtime;

namespace DeviceSocket.Cluster.Grains.Gateways;

/// <summary>
/// 保存单个网关实例的内部 RPC 地址和节点租约。
/// </summary>
public sealed class GatewayNodeGrain(
    [PersistentState("gateway", "GatewayStore")] IPersistentState<GatewayNodeState> state)
    : Grain, IGatewayNodeGrain
{
    /// <summary>
    /// 注册或续租网关节点；新 InstanceId 会完整替换上一次进程信息。
    /// </summary>
    public async Task<GatewayNodeSnapshot> RegisterOrRenewAsync(GatewayNodeRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.RpcEndpoint);

        var now = DateTimeOffset.UtcNow;
        state.State.Snapshot = new GatewayNodeSnapshot
        {
            GatewayNodeId = this.GetPrimaryKeyString(),
            InstanceId = registration.InstanceId,
            RpcEndpoint = registration.RpcEndpoint,
            SupportedProtocols = registration.SupportedProtocols.ToArray(),
            StartedAt = registration.StartedAt,
            LastHeartbeatAt = now,
            LeaseExpiresAt = registration.LeaseExpiresAt
        };
        await state.WriteStateAsync();
        return Copy(state.State.Snapshot);
    }

    /// <summary>
    /// 返回节点快照；调用方根据租约决定节点是否仍可用。
    /// </summary>
    public Task<GatewayNodeSnapshot> GetSnapshotAsync()
    {
        var snapshot = Copy(state.State.Snapshot);
        snapshot.GatewayNodeId = this.GetPrimaryKeyString();
        return Task.FromResult(snapshot);
    }

    private static GatewayNodeSnapshot Copy(GatewayNodeSnapshot source) => new()
    {
        GatewayNodeId = source.GatewayNodeId,
        InstanceId = source.InstanceId,
        RpcEndpoint = source.RpcEndpoint,
        SupportedProtocols = source.SupportedProtocols.ToArray(),
        StartedAt = source.StartedAt,
        LastHeartbeatAt = source.LastHeartbeatAt,
        LeaseExpiresAt = source.LeaseExpiresAt
    };
}
