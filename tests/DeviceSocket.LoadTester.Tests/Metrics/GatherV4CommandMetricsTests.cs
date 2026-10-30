using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Protocols.QGdw3761;
using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Tests.Metrics;

/// <summary>验证纯设备模式的命令观测无需 HTTP 登记，且重发、后续任务、故障和跨分片统计相互独立。</summary>
public sealed class GatherV4CommandMetricsTests
{
    /// <summary>同帧重发保留疑似语义，不同会话重用帧不继承旧确认状态。</summary>
    [Fact]
    public void PassiveCommands_TrackRepeatsAndFollowingCommandsWithinSession()
    {
        var metrics = new GatherV4CommandMetrics();
        var observer = new GatherV4SessionCommandObserver(metrics, 60);
        var now = DateTimeOffset.UtcNow;
        observer.Received("one", now);
        observer.Confirmed("one", now, now.AddMilliseconds(20));
        observer.Received("one", now.AddMilliseconds(30));
        observer.Received("two", now.AddMilliseconds(40));
        var newSession = new GatherV4SessionCommandObserver(metrics, 60);
        newSession.Received("one", now.AddSeconds(1));
        var snapshot = metrics.Snapshot();
        Assert.Equal(4, snapshot.ReceivedFrames);
        Assert.Equal(3, snapshot.ObservedCommands);
        Assert.Equal(1, snapshot.RepeatedAfterConfirmation);
        Assert.Equal(1, snapshot.FollowingCommands);
        var merged = GatherV4CommandSummary.Merge([snapshot, snapshot]);
        Assert.Equal(8, merged.ReceivedFrames);
        Assert.Equal(2, merged.ConfirmationLatency.Count);
    }

    /// <summary>长期运行时仅保存最近 500 个回复样本，避免逐设备原始报文无界增长。</summary>
    [Fact]
    public void ReplySamples_AreBounded()
    {
        var metrics = new GatherV4CommandMetrics();
        for (var i = 0; i < 700; i++)
            metrics.RecordSample(new GatherV4CommandSample { DeviceId = "3761:2608-00001", Outcome = "f1Sent", SessionGeneration = i });
        Assert.Equal(500, metrics.GetSamples().Count);
        Assert.Equal(200, metrics.GetSamples()[0].SessionGeneration);
    }
}
