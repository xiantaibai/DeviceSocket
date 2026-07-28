namespace DeviceSocket.Core.Protocols.Common;

/// <summary>
/// 描述一条协议错误及其可读说明。
/// </summary>
public sealed record ProtocolError(ProtocolErrorCode Code, string Message);
