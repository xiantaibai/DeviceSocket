using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Tests;

/// <summary>
/// 为 LoadTester 单元测试创建不访问外部服务且关闭默认验收门槛的最小有效配置。
/// </summary>
internal static class TestOptionsFactory
{
    internal static LoadTestOptions Create(LoadProtocol protocol = LoadProtocol.QGdw3761) => new()
    {
        Target = new TargetOptions { Host = "127.0.0.1", Port = protocol == LoadProtocol.QGdw3761 ? 5001 : 5002, Protocol = protocol },
        Devices = new DeviceOptions { Count = 10, AddressStart = 1, AdministrativeDivision = "3301" },
        Connection = new ConnectionOptions
        {
            StartMode = ConnectionStartMode.Burst,
            ConnectionsPerSecond = 100,
            MaxConcurrentConnecting = 10,
            ConnectTimeoutSeconds = 2,
            LoginTimeoutSeconds = 2,
            WriteTimeoutSeconds = 2
        },
        Heartbeat = new HeartbeatOptions { Enabled = true, IntervalSeconds = 2, JitterSeconds = 0, ResponseTimeoutSeconds = 1 },
        Workload = new WorkloadOptions { ReportsEnabled = false },
        Reconnect = new ReconnectOptions
        {
            Mode = ReconnectMode.Immediate,
            InitialDelayMilliseconds = 0,
            MaximumDelaySeconds = 1,
            MaximumAttempts = 2
        },
        Scenario = new ScenarioOptions { Name = "test", DurationSeconds = 1 },
        Reporting = new ReportingOptions { OutputDirectory = "test-results", SampleIntervalSeconds = 1, WriteConsoleSummary = false },
        Acceptance = new AcceptanceOptions
        {
            MinimumConnectionSuccessRate = null,
            MinimumLoginSuccessRate = null,
            MinimumFinalOnlineRate = null,
            MaximumProtocolErrorRate = null,
            MaximumRecoverySeconds = null
        },
        Distributed = new DistributedOptions { RunId = "unit-test", ShardIndex = 0, ShardCount = 1 }
    };
}
