using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Connections;
using DeviceSocket.Core.Runtime.Messages;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 把 SuperSocket 的 <see cref="IAppSession"/> 适配为核心层可使用的设备连接。
/// </summary>
public sealed class SuperSocketDeviceConnection(
    IAppSession session,
    DeviceProtocol protocol,
    string deviceId,
    IDeviceTrafficSink trafficSink) : IDeviceConnection
{
    public string SessionId => session.SessionID;

    public DeviceProtocol Protocol { get; } = protocol;

    public string? RemoteEndPoint => session.RemoteEndPoint?.ToString();

    /// <summary>
    /// 通过当前 Session 异步发送已经编码完成的二进制协议帧。
    /// </summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        await session.SendAsync(message, cancellationToken);
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
            audit?.AttemptNo));
    }
}
