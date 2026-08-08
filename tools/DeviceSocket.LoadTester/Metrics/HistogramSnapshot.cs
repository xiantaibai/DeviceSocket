namespace DeviceSocket.LoadTester.Metrics;

/// <summary>
/// 表示可序列化和跨节点求和的延迟直方图快照。
/// </summary>
public sealed record HistogramSnapshot(
    long Count,
    long SumMilliseconds,
    long MaximumMilliseconds,
    IReadOnlyList<HistogramBucketSnapshot> Buckets)
{
    /// <summary>
    /// 按直方图累计分布返回近似分位数的区间上界。
    /// </summary>
    public long Percentile(double percentile)
    {
        if (Count == 0)
            return 0;
        var target = (long)Math.Ceiling(Count * Math.Clamp(percentile, 0, 1));
        long accumulated = 0;
        foreach (var bucket in Buckets)
        {
            accumulated += bucket.Count;
            if (accumulated >= target)
                return bucket.UpperBoundMilliseconds;
        }

        return MaximumMilliseconds;
    }
}
