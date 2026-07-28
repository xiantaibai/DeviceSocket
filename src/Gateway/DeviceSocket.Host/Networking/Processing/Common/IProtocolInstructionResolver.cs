using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 将协议帧转换为可用于查找 Handler 的强类型指令键。
/// </summary>
public interface IProtocolInstructionResolver<TFrame, TInstructionKey>
    where TFrame : IProtocolFrame
    where TInstructionKey : notnull
{
    /// <summary>取得未注册指令应使用的默认处理键。</summary>
    TInstructionKey FallbackKey { get; }

    /// <summary>从已解析的协议帧中提取指令键。</summary>
    TInstructionKey Resolve(TFrame frame);
}
