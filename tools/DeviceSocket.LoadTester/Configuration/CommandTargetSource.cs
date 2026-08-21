using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 指定 HTTP 读表阶段从固定序号范围还是设备在线注册表选择请求目标。
/// </summary>
public enum CommandTargetSource
{
    /// <summary>
    /// 保持当前行为，按 <see cref="VirtualDeviceDescriptor.GlobalOrdinal"/> 固定截取目标设备。
    /// </summary>
    FixedOrdinal,

    /// <summary>
    /// 从本进程 TCP 会话维护的全部在线设备中动态选取阶段要求数量的目标设备。
    /// </summary>
    OnlineRegistry
}
