using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Tests.Reporting;

/// <summary>
/// 验证 Gateway 容量观测会扣除测试前已有连接，并保留服务不可用采样。
/// </summary>
public sealed class GatewayObservationAccumulatorTests
{
    /// <summary>
    /// 验证峰值增量以测试前基线计算，而绝对峰值仍可用于排查共享环境状态。
    /// </summary>
    [Fact]
    public void Snapshot_SubtractsPreExistingConnections()
    {
        var accumulator = new GatewayObservationAccumulator();
        accumulator.SetBaseline(new GatewayStatusSnapshot
        {
            Available = true,
            TcpConnections = 7,
            AuthenticatedDevices = 5,
            ApplicationLogDropped = 2,
            UpstreamLogDropped = 3,
            DownstreamLogDropped = 4,
            HeartbeatLogSuppressed = 100
        });
        accumulator.Record(new GatewayStatusSnapshot
        {
            Available = true,
            TcpConnections = 107,
            AuthenticatedDevices = 85,
            ApplicationLogDropped = 3,
            UpstreamLogDropped = 8,
            DownstreamLogDropped = 6,
            HeartbeatLogSuppressed = 1_100,
            ProcessCpuPercent = 72.5,
            ProcessWorkingSetBytes = 1_024,
            ManagedHeapBytes = 512,
            ThreadPoolPendingWorkItems = 9,
            PeakPendingSendMessages = 12,
            PeakPendingSendBytes = 4_096,
            MaximumSessionScanDurationMilliseconds = 25
        });

        var result = accumulator.Snapshot();

        Assert.Equal(7, result.BaselineTcpConnections);
        Assert.Equal(5, result.BaselineAuthenticatedDevices);
        Assert.Equal(107, result.MaximumTcpConnections);
        Assert.Equal(85, result.MaximumAuthenticatedDevices);
        Assert.Equal(100, result.MaximumAdditionalTcpConnections);
        Assert.Equal(80, result.MaximumAdditionalAuthenticatedDevices);
        Assert.Equal(72.5, result.MaximumProcessCpuPercent);
        Assert.Equal(1_024, result.MaximumProcessWorkingSetBytes);
        Assert.Equal(512, result.MaximumManagedHeapBytes);
        Assert.Equal(9, result.MaximumThreadPoolPendingWorkItems);
        Assert.Equal(12, result.MaximumPeakPendingSendMessages);
        Assert.Equal(4_096, result.MaximumPeakPendingSendBytes);
        Assert.Equal(25, result.MaximumSessionScanDurationMilliseconds);
        Assert.Equal(1, result.AdditionalApplicationLogDropped);
        Assert.Equal(5, result.AdditionalUpstreamLogDropped);
        Assert.Equal(2, result.AdditionalDownstreamLogDropped);
        Assert.Equal(1_000, result.AdditionalHeartbeatLogSuppressed);
    }

    /// <summary>
    /// 验证基线 API 不可用时不会把负载开始后的首次成功采样错误地当作零点。
    /// </summary>
    [Fact]
    public void UnavailableBaseline_DoesNotInferLateBaseline()
    {
        var accumulator = new GatewayObservationAccumulator();
        accumulator.SetBaseline(new GatewayStatusSnapshot { Available = false, Error = "offline" });
        accumulator.Record(new GatewayStatusSnapshot
        {
            Available = true,
            TcpConnections = 100,
            AuthenticatedDevices = 80
        });

        var result = accumulator.Snapshot();

        Assert.Null(result.BaselineTcpConnections);
        Assert.Null(result.BaselineAuthenticatedDevices);
        Assert.Equal(0, result.MaximumAdditionalTcpConnections);
        Assert.Equal(0, result.MaximumAdditionalAuthenticatedDevices);
        Assert.Equal(1, result.UnavailableSamples);
    }
}
