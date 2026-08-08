namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示一个阶段内的目标设备比例、连接速率和持续时间。
/// </summary>
public sealed class ScenarioPhaseOptions
{
    public string Name { get; set; } = "steady";
    public ScenarioPhaseKind Kind { get; set; } = ScenarioPhaseKind.Steady;
    public int DurationSeconds { get; set; } = 60;
    public double TargetDevicePercentage { get; set; } = 100;
    public int? ConnectionsPerSecond { get; set; }
    public FaultInjectionOptions Faults { get; set; } = new();
}
