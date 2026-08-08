namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示一轮可独立执行和跨节点复现的完整负载测试配置。
/// </summary>
public sealed class LoadTestOptions
{
    public TargetOptions Target { get; set; } = new();
    public DeviceOptions Devices { get; set; } = new();
    public ConnectionOptions Connection { get; set; } = new();
    public HeartbeatOptions Heartbeat { get; set; } = new();
    public WorkloadOptions Workload { get; set; } = new();
    public ReconnectOptions Reconnect { get; set; } = new();
    public ScenarioOptions Scenario { get; set; } = new();
    public ReportingOptions Reporting { get; set; } = new();
    public ServerObservationOptions ServerObservation { get; set; } = new();
    public AcceptanceOptions Acceptance { get; set; } = new();
    public DistributedOptions Distributed { get; set; } = new();
}
