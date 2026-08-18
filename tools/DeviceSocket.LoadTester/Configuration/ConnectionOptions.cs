namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示 TCP 建连速率、并发度以及连接和协议登录超时。
/// </summary>
public sealed class ConnectionOptions
{
    /// <summary>
    /// 获取或设置 <c>connection.startMode</c>；默认 <see cref="ConnectionStartMode.Ramp"/>，决定首次连接按限速爬升还是在并发上限内尽快突发。
    /// </summary>
    public ConnectionStartMode StartMode { get; set; } = ConnectionStartMode.Ramp;

    /// <summary>
    /// 获取或设置 <c>connection.connectionsPerSecond</c>；单位为所有分片合计连接数/秒，默认 100，零值无效；分片会自动换算本机连接速率。
    /// </summary>
    public int ConnectionsPerSecond { get; set; } = 100;

    /// <summary>
    /// 获取或设置 <c>connection.maxConcurrentConnecting</c>；单位为单个负载进程同时处于连接或登录中的设备数，默认 200，零值无效。
    /// </summary>
    public int MaxConcurrentConnecting { get; set; } = 200;

    /// <summary>
    /// 获取或设置 <c>connection.connectTimeoutSeconds</c>；单位秒，默认 5，限制单次 TCP 建连等待时间，零值无效。
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// 获取或设置 <c>connection.loginTimeoutSeconds</c>；单位秒，默认 5，限制发送协议登录帧后等待服务端确认的时间，零值无效。
    /// </summary>
    public int LoginTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// 获取或设置 <c>connection.writeTimeoutSeconds</c>；单位秒，默认 5，限制单次套接字写入及故障注入分片写入的总等待时间，零值无效。
    /// </summary>
    public int WriteTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// 获取或设置 <c>connection.noDelay</c>；默认 true，禁用 Nagle 合并以更贴近终端小报文实时发送，false 可能降低包数但增加指令延迟。
    /// </summary>
    public bool NoDelay { get; set; } = true;
}
