namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 标识单台虚拟设备在连接生命周期中发生超时的操作类别，用于分别统计服务端容量和通信稳定性问题。
/// </summary>
internal enum DeviceTimeoutKind
{
    /// <summary>TCP 三次握手在配置时限内未完成。</summary>
    Connect,
    /// <summary>协议登录帧发送后未在配置时限内收到有效确认。</summary>
    Login,
    /// <summary>套接字写入或故障注入分片写入在配置时限内未完成。</summary>
    Write,
    /// <summary>设备心跳发送后未在配置时限内收到有效确认。</summary>
    Heartbeat
}
