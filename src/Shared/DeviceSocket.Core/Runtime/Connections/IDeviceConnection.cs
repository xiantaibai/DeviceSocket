using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Core.Runtime.Connections;

/// <summary>
/// 对实际 Socket Session 的最小抽象，使核心层不依赖 SuperSocket 类型。
/// </summary>
public interface IDeviceConnection
{
    string SessionId { get; }

    DeviceProtocol Protocol { get; }

    string? RemoteEndPoint { get; }

    /// <summary>
    /// 向当前设备连接写入一条已经完成协议编码的消息。
    /// </summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
}
