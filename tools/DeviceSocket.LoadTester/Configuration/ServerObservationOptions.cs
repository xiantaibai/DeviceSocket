namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示可选 Gateway 状态 API 采样地址和请求超时，用于核对客户端与服务端在线数量。
/// </summary>
public sealed class ServerObservationOptions
{
    /// <summary>
    /// 获取或设置 <c>serverObservation.enabled</c>；默认 false，true 时定期请求状态接口并把服务端在线数写入时间序列和报告。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 获取或设置 <c>serverObservation.statusUrl</c>；默认本机 Gateway 连接状态接口，启用观测时必须是可访问的绝对 HTTP/HTTPS 地址。
    /// </summary>
    public string StatusUrl { get; set; } = "http://127.0.0.1:5080/api/status/connections";

    /// <summary>
    /// 获取或设置 <c>serverObservation.timeoutSeconds</c>；单位秒，默认 2，限制单次状态采样请求，零值无效；采样失败不会中断设备压测。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 2;
}
