namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 指定设备断线后的重连等待策略。
/// </summary>
public enum ReconnectMode
{
    Immediate,
    Fixed,
    ExponentialJitter
}
