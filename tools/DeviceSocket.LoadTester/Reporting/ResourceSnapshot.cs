namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示负载生成器自身的 CPU、内存、GC 和线程池资源快照。
/// </summary>
public sealed record ResourceSnapshot(
    DateTimeOffset TimestampUtc,
    double CpuPercent,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ThreadPoolThreads,
    long PendingWorkItems);
