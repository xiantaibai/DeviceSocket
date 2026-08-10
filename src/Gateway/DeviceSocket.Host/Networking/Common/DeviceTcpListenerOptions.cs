namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 定义单个设备 TCP 监听端点的地址、端口和连接等待队列参数。
/// </summary>
public sealed class DeviceTcpListenerOptions
{
    /// <summary>监听 IP；支持 <c>Any</c>、<c>IPv6Any</c> 或具体 IP 地址。</summary>
    public string Ip { get; set; } = "Any";

    /// <summary>TCP 监听端口，必须处于 1～65535。</summary>
    public int Port { get; set; }

    /// <summary>完成 TCP 握手后等待应用接受的最大连接数；该值不是在线连接上限。</summary>
    public int BackLog { get; set; } = 4_096;

    /// <summary>是否禁用 Nagle 算法；设备小报文交互默认禁用以降低发送延迟。</summary>
    public bool NoDelay { get; set; } = true;
}
