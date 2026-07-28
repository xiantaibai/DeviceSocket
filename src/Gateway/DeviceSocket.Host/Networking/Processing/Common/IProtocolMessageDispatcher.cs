using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 协议专属消息分发器的统一入口。
/// </summary>
public interface IProtocolMessageDispatcher<TFrame>
    where TFrame : IProtocolFrame
{
    /// <summary>完成协议自身的连接行为和指令 Handler 分发。</summary>
    ValueTask DispatchAsync(
        ProtocolMessageContext<TFrame> context,
        CancellationToken cancellationToken);
}
