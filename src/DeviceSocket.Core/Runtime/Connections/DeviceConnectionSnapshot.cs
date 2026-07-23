using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Core.Runtime.Connections;

/// <summary>
/// 提供给查询 API 使用的在线设备快照，不暴露实际 Session 对象。
/// </summary>
public sealed record DeviceConnectionSnapshot(
    string DeviceId,
    string SessionId,
    DeviceProtocol Protocol,
    string? RemoteEndPoint,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastSeenAt);
