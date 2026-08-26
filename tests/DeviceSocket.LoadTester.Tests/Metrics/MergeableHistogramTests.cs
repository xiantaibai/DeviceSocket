using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Tests.Metrics;

/// <summary>
/// 验证延迟直方图的分位数和多节点合并语义。
/// </summary>
public sealed class MergeableHistogramTests
{
    /// <summary>
    /// 验证合并后保留总样本数、最大值和近似分位数。
    /// </summary>
    [Fact]
    public void Merge_PreservesDistribution()
    {
        var first = new MergeableHistogram();
        first.Record(TimeSpan.FromMilliseconds(10));
        first.Record(TimeSpan.FromMilliseconds(100));
        var second = new MergeableHistogram();
        second.Record(TimeSpan.FromMilliseconds(500));

        var merged = MergeableHistogram.Merge([first.Snapshot(), second.Snapshot()]);

        Assert.Equal(3, merged.Count);
        Assert.Equal(500, merged.MaximumMilliseconds);
        Assert.Equal(500, merged.Percentile(0.99));
    }

    /// <summary>
    /// 验证一分钟附近的同步 HTTP 延迟不会再被粗粒度直方图夸大到 120 秒。
    /// </summary>
    [Fact]
    public void Snapshot_UsesFineBucketsAroundOneMinute()
    {
        var histogram = new MergeableHistogram();
        histogram.Record(TimeSpan.FromMilliseconds(61_897));

        var snapshot = histogram.Snapshot();

        Assert.Equal(65_000, snapshot.Percentile(0.95));
        Assert.Equal(61_897, snapshot.MaximumMilliseconds);
    }
}
