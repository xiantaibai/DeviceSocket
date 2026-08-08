namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示除协议心跳之外的周期设备上报负载。
/// </summary>
public sealed class WorkloadOptions
{
    public bool ReportsEnabled { get; set; }
    public int ReportIntervalSeconds { get; set; } = 300;
    public int JitterSeconds { get; set; } = 10;
}
