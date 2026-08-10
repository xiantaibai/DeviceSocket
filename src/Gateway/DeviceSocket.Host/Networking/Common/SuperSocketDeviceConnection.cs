using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Connections;
using DeviceSocket.Core.Runtime.Messages;

namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 把 SuperSocket 的 <see cref="IAppSession"/> 适配为核心层可使用的设备连接。
/// </summary>
public sealed class SuperSocketDeviceConnection(
    string sessionId,
    DeviceProtocol protocol,
    string deviceId,
    string? remoteEndPoint,
    DeviceTcpSessionManager sessionManager,
    IDeviceTrafficSink trafficSink) : IDeviceConnection
{
    public string SessionId { get; } = sessionId;

    public DeviceProtocol Protocol { get; } = protocol;

    public string? RemoteEndPoint { get; } = remoteEndPoint;

    /// <summary>
    /// 通过当前 Session 异步发送已经编码完成的二进制协议帧。
    /// </summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        await sessionManager.SendAsync(SessionId, message, cancellationToken);
        var audit = DeviceSendAuditContext.Current;
        trafficSink.TryWrite(new DeviceTrafficRecord(
            DateTimeOffset.UtcNow,
            audit?.MsgId ?? Guid.NewGuid().ToString("N"),
            MessageDirection.Downstream,
            Protocol,
            deviceId,
            SessionId,
            message.Length,
            HexCodec.ToHex(message.Span),
            audit?.TraceId,
            audit?.Source ?? "GatewayDirectSend",
            audit?.AttemptNo,
            ProtocolTrafficKind.Business));
    }
}
