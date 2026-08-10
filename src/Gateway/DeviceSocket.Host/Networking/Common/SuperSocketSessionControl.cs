using System.Net;
using SuperSocket.Connection;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 将 SuperSocket <see cref="IAppSession"/> 适配为统一设备 Session 控制接口。
/// </summary>
public sealed class SuperSocketSessionControl(IAppSession session) : IDeviceSessionControl
{
    /// <inheritdoc />
    public string SessionId => session.SessionID;

    /// <inheritdoc />
    public string? RemoteAddress => session.RemoteEndPoint is IPEndPoint endpoint
        ? NormalizeAddress(endpoint.Address)
        : session.RemoteEndPoint?.ToString();

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
        session.SendAsync(message, cancellationToken);

    /// <inheritdoc />
    public ValueTask CloseAsync(DeviceSessionCloseReason reason) =>
        session.CloseAsync(MapCloseReason(reason));

    private static string NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

    private static CloseReason MapCloseReason(DeviceSessionCloseReason reason) => reason switch
    {
        DeviceSessionCloseReason.LoginTimeout or
        DeviceSessionCloseReason.IdleTimeout or
        DeviceSessionCloseReason.SendTimeout => CloseReason.TimeOut,
        DeviceSessionCloseReason.ProtocolErrorLimit or
        DeviceSessionCloseReason.StaleSession => CloseReason.ProtocolError,
        DeviceSessionCloseReason.SendBackpressure => CloseReason.ApplicationError,
        _ => CloseReason.LocalClosing
    };
}
