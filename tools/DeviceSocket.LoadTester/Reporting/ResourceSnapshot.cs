namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示负载生成器自身的 CPU、内存、GC、线程池、进程线程和句柄资源快照。
/// </summary>
/// <param name="TimestampUtc">采样 UTC 时刻。</param>
/// <param name="CpuPercent">自上次采样以来按逻辑处理器数量归一化的进程 CPU 百分比。</param>
/// <param name="WorkingSetBytes">进程工作集字节数。</param>
/// <param name="ManagedHeapBytes">GC 报告的托管堆字节数。</param>
/// <param name="Gen0Collections">进程启动以来第 0 代 GC 次数。</param>
/// <param name="Gen1Collections">进程启动以来第 1 代 GC 次数。</param>
/// <param name="Gen2Collections">进程启动以来第 2 代 GC 次数。</param>
/// <param name="ThreadPoolThreads">托管线程池当前线程数。</param>
/// <param name="PendingWorkItems">托管线程池当前等待处理的工作项数。</param>
public sealed record ResourceSnapshot(
    DateTimeOffset TimestampUtc,
    double CpuPercent,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ThreadPoolThreads,
    long PendingWorkItems)
{
    /// <summary>获取 LoadTester 进程当前操作系统线程数；采样平台不支持时为 0。</summary>
    public int ProcessThreadCount { get; init; }

    /// <summary>获取 LoadTester 进程当前句柄数；非 Windows 或采样平台不支持时为 0。</summary>
    public int ProcessHandleCount { get; init; }
}
