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
}
