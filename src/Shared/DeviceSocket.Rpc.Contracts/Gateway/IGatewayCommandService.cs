using MagicOnion;

namespace DeviceSocket.Rpc.Contracts.Gateway;

/// <summary>
/// 网关内部 MagicOnion Unary 服务，只负责向本节点持有的设备连接写入报文。
/// </summary>
public interface IGatewayCommandService : IService<IGatewayCommandService>
{
    /// <summary>
    /// 校验设备路由并写入完整协议报文；通信异常由调用方使用同一 MsgId 重试。
    /// </summary>
    /// <param name="request">包含设备路由围栏和原始报文的请求。</param>
    /// <returns>确定性的网关受理或失败状态，不代表设备已经业务确认。</returns>
    UnaryResult<SendCommandResponse> SendCommandAsync(SendCommandRequest request);
}
