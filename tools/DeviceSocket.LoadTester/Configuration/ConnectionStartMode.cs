namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 指定初始设备连接的流量形态。
/// </summary>
public enum ConnectionStartMode
{
    /// <summary>
    /// 按 <c>connection.connectionsPerSecond</c> 平滑启动，适合容量爬升和稳定性基线测试。
    /// </summary>
    Ramp,

    /// <summary>
    /// 在 <c>connection.maxConcurrentConnecting</c> 约束内尽快发起连接，适合模拟服务重启后的暴风重连。
    /// </summary>
    Burst
}
