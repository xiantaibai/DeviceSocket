namespace DeviceSocket.LoadTester.Metrics;

/// <summary>
/// 表示可跨节点合并的一个延迟直方图区间及其累计样本数。
/// </summary>
public sealed record HistogramBucketSnapshot(long UpperBoundMilliseconds, long Count);
