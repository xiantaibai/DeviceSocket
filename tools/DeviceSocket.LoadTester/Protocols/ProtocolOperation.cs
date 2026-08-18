namespace DeviceSocket.LoadTester.Protocols;

/// <summary>
/// 标识需要与服务端确认帧严格关联的设备链路操作。
/// </summary>
internal enum ProtocolOperation
{
    /// <summary>设备首次建立协议会话的登录操作。</summary>
    Login,
    /// <summary>设备在线期间续租链路的心跳操作。</summary>
    Heartbeat
}
