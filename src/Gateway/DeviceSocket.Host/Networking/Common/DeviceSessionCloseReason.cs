namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 表示 Gateway 主动关闭设备 TCP Session 的业务原因。
/// </summary>
public enum DeviceSessionCloseReason
{
    /// <summary>连接建立后没有在限定时间内完成协议登录或设备绑定。</summary>
    LoginTimeout,

    /// <summary>已认证连接超过限定时间没有收到合法协议帧。</summary>
    IdleTimeout,

    /// <summary>连续非法协议帧达到配置阈值。</summary>
    ProtocolErrorLimit,

    /// <summary>同一设备的新 Session 已经替换当前 Session。</summary>
    DuplicateLogin,

    /// <summary>设备发送退出登录指令，确认完成后关闭连接。</summary>
    Logout,

    /// <summary>下发等待或写入 Socket 超时。</summary>
    SendTimeout,

    /// <summary>下发队列超过单 Session 或 Gateway 的有界背压限制。</summary>
    SendBackpressure,

    /// <summary>未登录或已经失效的 Session 继续发送需要在线身份的指令。</summary>
    StaleSession
}
