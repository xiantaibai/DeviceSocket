using DeviceSocket.Core.Protocols.Common;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 传递给协议指令 Handler 的单条上行消息上下文。
/// </summary>
public sealed record ProtocolMessageContext<TFrame>(
    IAppSession Session,
    TFrame Frame,
    string MsgId,
    DateTimeOffset ReceivedAt)
    where TFrame : IProtocolFrame;
