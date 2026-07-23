using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Core.Runtime.Messages;

/// <summary>
/// 最近接收到的合法设备消息记录。
/// </summary>
public sealed record ReceivedDeviceMessage(
    DateTimeOffset ReceivedAt,
    DeviceProtocol Protocol,
    string? DeviceId,
    string SessionId,
    string? RemoteEndPoint,
    string Summary,
    string RawHex);
