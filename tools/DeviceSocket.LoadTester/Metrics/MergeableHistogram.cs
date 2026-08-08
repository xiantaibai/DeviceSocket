namespace DeviceSocket.LoadTester.Metrics;

/// <summary>
/// 使用固定毫秒区间记录延迟，避免保存每个设备的原始样本并支持多机无损求和。
/// </summary>
public sealed class MergeableHistogram
{
    private static readonly long[] UpperBounds =
    [
        1, 2, 5, 10, 20, 50, 100, 200, 500, 1_000, 2_000, 5_000,
        10_000, 30_000, 60_000, 120_000, 300_000, long.MaxValue
    ];

    private readonly long[] _counts = new long[UpperBounds.Length];
    private long _count;
    private long _sum;
    private long _maximum;

    /// <summary>
    /// 记录一个非负毫秒样本；超出五分钟的值归入最后一个开放区间。
    /// </summary>
    public void Record(TimeSpan elapsed)
    {
        var milliseconds = Math.Max(0, (long)Math.Ceiling(elapsed.TotalMilliseconds));
        var index = Array.BinarySearch(UpperBounds, milliseconds);
        if (index < 0)
            index = ~index;
        Interlocked.Increment(ref _counts[Math.Min(index, _counts.Length - 1)]);
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sum, milliseconds);
        UpdateMaximum(milliseconds);
    }

    /// <summary>
    /// 获取不会阻塞采样线程的原子计数快照。
    /// </summary>
    public HistogramSnapshot Snapshot()
    {
        var buckets = new HistogramBucketSnapshot[UpperBounds.Length];
        for (var index = 0; index < UpperBounds.Length; index++)
        {
            var upperBound = UpperBounds[index] == long.MaxValue
                ? Math.Max(300_001, Interlocked.Read(ref _maximum))
                : UpperBounds[index];
            buckets[index] = new HistogramBucketSnapshot(upperBound, Interlocked.Read(ref _counts[index]));
        }

        return new HistogramSnapshot(
            Interlocked.Read(ref _count),
            Interlocked.Read(ref _sum),
            Interlocked.Read(ref _maximum),
            buckets);
    }

    /// <summary>
    /// 合并多个节点生成的同构直方图快照。
    /// </summary>
    public static HistogramSnapshot Merge(IEnumerable<HistogramSnapshot> snapshots)
    {
        var source = snapshots.ToArray();
        if (source.Length == 0)
            return new MergeableHistogram().Snapshot();

        var bucketCount = source.Max(item => item.Buckets.Count);
        var buckets = new List<HistogramBucketSnapshot>(bucketCount);
        for (var index = 0; index < bucketCount; index++)
        {
            var available = source.Where(item => index < item.Buckets.Count).ToArray();
            buckets.Add(new HistogramBucketSnapshot(
                available.Max(item => item.Buckets[index].UpperBoundMilliseconds),
                available.Sum(item => item.Buckets[index].Count)));
        }

        return new HistogramSnapshot(
            source.Sum(item => item.Count),
            source.Sum(item => item.SumMilliseconds),
            source.Max(item => item.MaximumMilliseconds),
            buckets);
    }

    private void UpdateMaximum(long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _maximum);
            if (value <= current || Interlocked.CompareExchange(ref _maximum, value, current) == current)
                return;
        }
    }
}
