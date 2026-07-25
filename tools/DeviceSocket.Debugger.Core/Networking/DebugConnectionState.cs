namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 表示 TCP 调试会话的连接状态。
/// </summary>
public enum DebugConnectionState
{
    /// <summary>尚未连接。</summary>
    Disconnected,

    /// <summary>正在建立连接。</summary>
    Connecting,

    /// <summary>连接已经建立。</summary>
    Connected,

    /// <summary>正在主动断开。</summary>
    Disconnecting
}
