using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Runtime;

namespace DeviceSocket.LoadTester.Tests.Runtime;

/// <summary>
/// 验证 TCP 场景运行器在正常和异常收尾时保留可用于最终报告的执行现场。
/// </summary>
public sealed class LoadTestRunnerTests
{
    /// <summary>
    /// 验证并行 HTTP 工作负载异常时会在设备清理前保存中止快照，调用方能够生成非空部分报告。
    /// </summary>
    [Fact]
    public async Task RunAsync_WorkloadFailure_PreservesLastExecution()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 1;
        options.Scenario.Phases =
        [
            new ScenarioPhaseOptions
            {
                Name = "command-hold",
                Kind = ScenarioPhaseKind.Steady,
                DurationSeconds = 1,
                TargetDevicePercentage = 0
            }
        ];
        var devices = DeviceAddressAllocator.Allocate(options);
        var metrics = new LoadTestMetrics();
        var onlineRegistry = new DeviceOnlineRegistry();
        using var pacer = new ConnectionPacer(0, metrics);
        using var gate = new SemaphoreSlim(1);
        var runner = new LoadTestRunner(
            options,
            devices,
            LoadTestProtocolFactory.Create(options),
            metrics,
            onlineRegistry,
            pacer,
            gate,
            new FaultInjectionController());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(
                _ => Task.FromException(new InvalidOperationException("HTTP 阶段失败")),
                CancellationToken.None));

        Assert.NotNull(runner.LastExecution);
        Assert.Equal("command-hold", runner.LastExecution.Measurement.CurrentPhaseName);
        Assert.False(runner.LastExecution.Cancelled);
    }
}
