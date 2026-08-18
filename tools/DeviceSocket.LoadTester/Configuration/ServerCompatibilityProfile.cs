namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 指定同一通信协议面向不同服务端实现时采用的兼容行为。
/// </summary>
public enum ServerCompatibilityProfile
{
    /// <summary>
    /// 使用当前 DeviceSocket 服务端的标准确认和关联规则。
    /// </summary>
    DeviceSocket,

    /// <summary>
    /// 兼容 Gather.TcpCenter 老服务的376.1确认帧和指令关联规则。
    /// </summary>
    GatherTcpCenter
}
