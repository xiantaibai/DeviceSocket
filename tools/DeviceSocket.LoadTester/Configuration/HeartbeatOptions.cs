namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示设备在线后的心跳频率、随机抖动和确认超时。
/// </summary>
public sealed class HeartbeatOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;
    public int JitterSeconds { get; set; } = 5;
    public int ResponseTimeoutSeconds { get; set; } = 5;
}
