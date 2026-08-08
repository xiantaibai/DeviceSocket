namespace DeviceSocket.LoadTester.Devices;

/// <summary>
/// 表示虚拟设备从创建到重连的可观测生命周期状态。
/// </summary>
public enum VirtualDeviceState
{
    Created,
    WaitingToConnect,
    Connecting,
    LoggingIn,
    Online,
    Backoff,
    Stopped
}
