namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 定义 Gateway 设备 TCP 连接的容量、超时、Socket 和发送背压参数。
/// </summary>
public sealed class DeviceConnectionOptions
{
    /// <summary>配置节名称；沿用既有连接上限路径以保持部署配置兼容。</summary>
    public const string SectionName = "DeviceSocket:ConnectionLimit";

    /// <summary>配置路径 <c>DeviceSocket:ConnectionLimit:MaxConnections</c>；单位为条，默认 85,000，必须大于零，为 80,000 条目标连接预留约 6% 的接入余量。</summary>
    public int MaxConnections { get; set; } = 85_000;

    /// <summary>完成协议登录或设备绑定前允许占用的最大 TCP 连接数。</summary>
    public int MaxUnauthenticatedConnections { get; set; } = 5_000;

    /// <summary>单一远端 IP 允许同时建立的连接数；零表示不限制，以兼容大规模 NAT 出口。</summary>
    public int MaxConnectionsPerIp { get; set; }

    /// <summary>每个自然秒允许接入的最大连接数；零表示不限制。</summary>
    public int MaxConnectionsPerSecond { get; set; } = 5_000;

    /// <summary>Session 建立后完成协议登录或设备绑定的最长秒数。</summary>
    public int LoginTimeoutSeconds { get; set; } = 30;

    /// <summary>已认证 Session 连续没有合法协议帧时允许保持连接的最长秒数。</summary>
    public int IdleTimeoutSeconds { get; set; } = 420;

    /// <summary>SuperSocket 传输层空闲清理和协议层 Session 看门狗共用的扫描间隔秒数。</summary>
    public int IdleScanIntervalSeconds { get; set; } = 30;

    /// <summary>单次扫描最多并发关闭的 Session 数量。</summary>
    public int MaxConcurrentSessionCloses { get; set; } = 128;

    /// <summary>连接连续出现多少条非法协议帧后主动断开。</summary>
    public int MaxConsecutiveProtocolErrors { get; set; } = 5;

    /// <summary>配置路径 <c>DeviceSocket:ConnectionLimit:SendTimeoutSeconds</c>；单位为秒，默认 15 秒且必须大于零，Gateway 通过取消令牌限制等待 SuperSocket 发送锁及异步 Pipe 写入，超时后关闭当前 Session。</summary>
    public int SendTimeoutSeconds { get; set; } = 15;

    /// <summary>单个 Session 允许同时等待或执行的最大下发消息数。</summary>
    public int MaxPendingSendMessagesPerSession { get; set; } = 16;

    /// <summary>单个 Session 允许同时等待或执行的最大下发字节数。</summary>
    public int MaxPendingSendBytesPerSession { get; set; } = 65_536;

    /// <summary>整个 Gateway 允许同时等待或执行的最大下发字节数。</summary>
    public long MaxPendingSendBytes { get; set; } = 268_435_456;

    /// <summary>SuperSocket 每条连接的接收缓冲区大小 4KB。</summary>
    public int ReceiveBufferSize { get; set; } = 4_096;

    /// <summary>SuperSocket 每条连接的发送缓冲区大小。</summary>
    public int SendBufferSize { get; set; } = 4_096;

    /// <summary>单条协议包处理允许占用的最长秒数。</summary>
    public int PackageHandlingTimeoutSeconds { get; set; } = 10;

    /// <summary>是否为设备 TCP Socket 启用传输层 KeepAlive 探测。</summary>
    public bool TcpKeepAliveEnabled { get; set; } = true;

    /// <summary>Socket 空闲多久后开始发送 TCP KeepAlive 探测。</summary>
    public int TcpKeepAliveTimeSeconds { get; set; } = 180;

    /// <summary>相邻 TCP KeepAlive 探测之间的间隔秒数。</summary>
    public int TcpKeepAliveIntervalSeconds { get; set; } = 30;

    /// <summary>TCP KeepAlive 连续失败多少次后由操作系统判定连接失效。</summary>
    public int TcpKeepAliveRetryCount { get; set; } = 3;
}
