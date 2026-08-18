using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Tests.Metrics;

/// <summary>
/// 验证在线目标变化和服务中断恢复窗口的状态语义。
/// </summary>
public sealed class LoadTestMetricsTests
{
    /// <summary>
    /// 验证故障阶段切换到恢复阶段且目标不变时，停机计时不会被阶段边界清零。
    /// </summary>
    [Fact]
    public void SameExpectedOnline_PreservesRecoveryWindow()
    {
        var metrics = new LoadTestMetrics();
        var start = DateTimeOffset.UtcNow;
        metrics.SetExpectedOnline(1, start);
        metrics.LoginCompleted(TimeSpan.FromMilliseconds(10), start);
        metrics.ConnectionClosed(wasOnline: true, start.AddSeconds(1));

        metrics.SetExpectedOnline(1, start.AddSeconds(2));
        metrics.LoginCompleted(TimeSpan.FromMilliseconds(10), start.AddSeconds(3));

        var snapshot = metrics.Snapshot(start.AddSeconds(3));
        Assert.Equal(2_000, snapshot.LastRecoveryMilliseconds);
        Assert.False(snapshot.OutageActive);
    }

    /// <summary>
    /// 验证 HTTP 业务失败、超时和 Gather 读表业务响应均进入独立计数与延迟直方图。
    /// </summary>
    [Fact]
    public void CommandMetrics_AreRecordedIndependently()
    {
        var metrics = new LoadTestMetrics();
        metrics.ApiRequestStarted();
        metrics.ApiRequestCompleted(true, TimeSpan.FromMilliseconds(12), null);
        metrics.ApiRequestStarted();
        metrics.ApiRequestTimedOut(TimeSpan.FromSeconds(70));
        metrics.GatherReadingCommandReceived();
        metrics.GatherReadingResponseSent(businessFailure: false, duplicate: false);
        metrics.GatherReadingCommandResponded(TimeSpan.FromMilliseconds(8));

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);

        Assert.Equal(2, snapshot.ApiRequestsStarted);
        Assert.Equal(1, snapshot.ApiRequestsSucceeded);
        Assert.Equal(1, snapshot.ApiRequestsFailed);
        Assert.Equal(1, snapshot.ApiRequestTimeouts);
        Assert.Equal(2, snapshot.ApiRequestLatency.Count);
        Assert.Equal(1, snapshot.GatherReadingCommandsReceived);
        Assert.Equal(1, snapshot.GatherReadingCommandsResponded);
        Assert.Equal(1, snapshot.GatherReadingResponsesSent);
    }
}
