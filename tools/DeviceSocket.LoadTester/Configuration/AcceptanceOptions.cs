namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示测试结束时用于自动判定成功或失败的可选质量门槛。
/// </summary>
public sealed class AcceptanceOptions
{
    public double? MinimumConnectionSuccessRate { get; set; }
    public double? MinimumLoginSuccessRate { get; set; }
    public double? MinimumFinalOnlineRate { get; set; } = 0.95;
    public double? MaximumProtocolErrorRate { get; set; } = 0.01;
    public double? MaximumRecoverySeconds { get; set; }
    public int? MinimumFinalOnlineCount { get; set; }
    public int? MaximumFinalOnlineCount { get; set; }
    public long? MinimumReconnects { get; set; }
    public int? MinimumObservedAuthenticatedDevices { get; set; }
    public int? MaximumObservedAuthenticatedDevices { get; set; }
}
