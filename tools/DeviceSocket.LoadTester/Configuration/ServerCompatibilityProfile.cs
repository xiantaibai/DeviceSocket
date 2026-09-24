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
    /// 兼容 Gather.TcpCenter 老服务的376.1确认帧、指令关联规则，以及 CSV 真实设备映射中的特殊终端地址 <c>00000</c>。
    /// </summary>
    GatherTcpCenter,

    /// <summary>
    /// 兼容 GatherV4 通讯服务的 CON=1 链路请求、AFN=00H/F1 确认以及按设备编号、A3、PSEQ 关联的下行指令。
    /// </summary>
    GatherV4
}
