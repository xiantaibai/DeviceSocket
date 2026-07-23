using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Core.Runtime.Messages;

/// <summary>
/// 协议、处理器或发送阶段产生的结构化错误事件。
/// </summary>
public sealed record DeviceErrorEvent(
    DateTimeOffset OccurredAt,
    string Category,
    string Code,
    string Message,
    DeviceProtocol? Protocol = null,
    string? DeviceId = null,
    string? SessionId = null,
    string? RemoteEndPoint = null,
    string? RawHex = null,
    string? TraceId = null);
