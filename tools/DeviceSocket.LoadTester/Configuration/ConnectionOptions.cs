namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示 TCP 建连速率、并发度以及连接和协议登录超时。
/// </summary>
public sealed class ConnectionOptions
{
    public ConnectionStartMode StartMode { get; set; } = ConnectionStartMode.Ramp;
    public int ConnectionsPerSecond { get; set; } = 100;
    public int MaxConcurrentConnecting { get; set; } = 200;
    public int ConnectTimeoutSeconds { get; set; } = 5;
    public int LoginTimeoutSeconds { get; set; } = 5;
    public int WriteTimeoutSeconds { get; set; } = 5;
    public bool NoDelay { get; set; } = true;
}
