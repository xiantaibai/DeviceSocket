using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Connections;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 把 SuperSocket 的 <see cref="IAppSession"/> 适配为核心层可使用的设备连接。
/// </summary>
public sealed class SuperSocketDeviceConnection(IAppSession session, DeviceProtocol protocol) : IDeviceConnection
{
    public string SessionId => session.SessionID;

    public DeviceProtocol Protocol { get; } = protocol;

    public string? RemoteEndPoint => session.RemoteEndPoint?.ToString();

    /// <summary>
    /// 通过当前 Session 异步发送已经编码完成的二进制协议帧。
    /// </summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
        session.SendAsync(message, cancellationToken);
}
