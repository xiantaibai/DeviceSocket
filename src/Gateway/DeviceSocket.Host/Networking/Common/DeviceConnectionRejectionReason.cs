namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 表示设备 TCP 连接在注册为 SuperSocket Session 前被拒绝的原因。
/// </summary>
public enum DeviceConnectionRejectionReason
{
    /// <summary>连接未被拒绝。</summary>
    None,

    /// <summary>Gateway 总连接数已经达到上限。</summary>
    Capacity,

    /// <summary>尚未完成协议认证的连接数已经达到上限。</summary>
    UnauthenticatedCapacity,

    /// <summary>同一远端 IP 的并发连接数已经达到上限。</summary>
    PerIpCapacity,

    /// <summary>当前自然秒内的接入速率已经达到上限。</summary>
    RateLimit
}
