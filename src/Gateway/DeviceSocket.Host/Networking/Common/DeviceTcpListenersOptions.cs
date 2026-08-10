namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 定义 Gateway 内各设备协议的独立 TCP 监听配置。
/// </summary>
public sealed class DeviceTcpListenersOptions
{
    /// <summary>设备 TCP 监听配置节名称。</summary>
    public const string SectionName = "DeviceSocket:Listeners";

    /// <summary>Q/GDW 376.1 协议监听配置。</summary>
    public DeviceTcpListenerOptions QGdw3761 { get; set; } = new()
    {
        Port = 5_001
    };

    /// <summary>DL/T 645-2007 协议监听配置。</summary>
    public DeviceTcpListenerOptions Dlt645 { get; set; } = new()
    {
        Port = 5_002
    };
}
