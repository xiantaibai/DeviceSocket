namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 抽象一条可发送和关闭的设备 TCP Session，便于统一治理且隔离 SuperSocket 具体类型。
/// </summary>
public interface IDeviceSessionControl
{
    /// <summary>取得 SuperSocket Session 唯一编号。</summary>
    string SessionId { get; }

    /// <summary>取得用于连接计数与诊断的远端 IP 地址。</summary>
    string? RemoteAddress { get; }

    /// <summary>
    /// 向底层 Socket 写入一条完整协议帧；取消或网络异常会传播给调用方。
    /// </summary>
    /// <param name="message">已经完成协议编码的二进制帧。</param>
    /// <param name="cancellationToken">等待写入时使用的取消令牌。</param>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    /// <summary>
    /// 按业务原因主动关闭底层 Session；重复调用必须保持幂等。
    /// </summary>
    /// <param name="reason">Gateway 判定的主动关闭原因。</param>
    ValueTask CloseAsync(DeviceSessionCloseReason reason);
}
