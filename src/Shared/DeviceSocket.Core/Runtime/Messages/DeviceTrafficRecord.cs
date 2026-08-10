using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Core.Runtime.Messages;

/// <summary>
/// 写入独立通信日志文件的单条设备报文记录。
/// </summary>
public sealed record DeviceTrafficRecord(
    DateTimeOffset OccurredAt,
    string MsgId,
    MessageDirection Direction,
    DeviceProtocol Protocol,
    string? DeviceId,
    string SessionId,
    int Bytes,
    string RawHex,
    string? TraceId = null,
    string? Source = null,
    int? AttemptNo = null,
    ProtocolTrafficKind TrafficKind = ProtocolTrafficKind.Business);
