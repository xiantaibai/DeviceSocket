namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示设备异常离线后的重连次数、退避上限和随机化规则。
/// </summary>
public sealed class ReconnectOptions
{
    public ReconnectMode Mode { get; set; } = ReconnectMode.ExponentialJitter;
    public int InitialDelayMilliseconds { get; set; } = 500;
    public int MaximumDelaySeconds { get; set; } = 30;
    public double Multiplier { get; set; } = 2;
    public double JitterRatio { get; set; } = 0.2;
    public int MaximumAttempts { get; set; }
}
