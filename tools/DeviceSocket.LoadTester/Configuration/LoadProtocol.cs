namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 指定负载测试虚拟设备使用的通信协议。
/// </summary>
public enum LoadProtocol
{
    /// <summary>
    /// Q/GDW 376.1 集中器与主站通信协议；可结合兼容配置连接新 DeviceSocket 或老 Gather.TcpCenter 服务。
    /// </summary>
    QGdw3761,

    /// <summary>
    /// DL/T 645-2007 多功能电能表通信协议；使用十二位表地址生成虚拟终端身份。
    /// </summary>
    Dlt6452007
}
