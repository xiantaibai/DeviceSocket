using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 不同设备协议指令处理器的统一泛型契约。
/// </summary>
/// <typeparam name="TFrame">协议完整帧类型。</typeparam>
/// <typeparam name="TInstructionKey">协议专属的强类型指令键。</typeparam>
public interface IProtocolInstructionHandler<TFrame, TInstructionKey>
    where TFrame : IProtocolFrame
    where TInstructionKey : notnull
{
    /// <summary>取得当前 Handler 唯一对应的指令键。</summary>
    TInstructionKey Key { get; }

    /// <summary>处理一条已通过协议校验和公共记录的设备消息。</summary>
    ValueTask HandleAsync(
        ProtocolMessageContext<TFrame> context,
        CancellationToken cancellationToken);
}
