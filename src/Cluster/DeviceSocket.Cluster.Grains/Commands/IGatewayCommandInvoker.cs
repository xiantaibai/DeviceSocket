using DeviceSocket.Rpc.Contracts.Gateway;

namespace DeviceSocket.Cluster.Grains.Commands;

/// <summary>
/// 根据 Orleans 路由调用指定 Gateway 的内部下发服务。
/// </summary>
public interface IGatewayCommandInvoker
{
    /// <summary>
    /// 通过复用的 MagicOnion Channel 调用目标网关；超时或连接异常由实现抛出给 DeviceGrain 分类处理。
    /// </summary>
    Task<SendCommandResponse> SendAsync(string rpcEndpoint, SendCommandRequest request);
}
