namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 提供 TCP 调试会话状态变化后的新状态和说明。
/// </summary>
public sealed class DebugConnectionStateChangedEventArgs(
    DebugConnectionState state,
    string message) : EventArgs
{
    /// <summary>获取变化后的连接状态。</summary>
    public DebugConnectionState State { get; } = state;

    /// <summary>获取适合界面展示的状态说明。</summary>
    public string Message { get; } = message;
}
