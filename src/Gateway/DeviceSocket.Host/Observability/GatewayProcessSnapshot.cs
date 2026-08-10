namespace DeviceSocket.Host.Observability;

/// <summary>
/// 表示 Gateway 进程在单次采样时的 CPU、内存、GC、线程池和句柄状态。
/// </summary>
/// <param name="CpuPercent">自上次采样以来按逻辑处理器归一化的进程 CPU 使用率。</param>
/// <param name="WorkingSetBytes">当前进程工作集字节数。</param>
/// <param name="ManagedHeapBytes">当前托管堆估算字节数。</param>
/// <param name="GcCommittedBytes">GC 已提交内存字节数。</param>
/// <param name="GcPauseTimePercentage">最近 GC 内存信息中的暂停时间百分比。</param>
/// <param name="Gen0Collections">第 0 代 GC 累计次数。</param>
/// <param name="Gen1Collections">第 1 代 GC 累计次数。</param>
/// <param name="Gen2Collections">第 2 代 GC 累计次数。</param>
/// <param name="ThreadPoolThreads">当前线程池线程数。</param>
/// <param name="ThreadPoolPendingWorkItems">当前线程池待处理工作项数。</param>
/// <param name="ThreadPoolAvailableWorkerThreads">当前可用线程池工作线程数。</param>
/// <param name="ThreadPoolAvailableIoThreads">当前可用线程池异步 I/O 线程数。</param>
/// <param name="HandleCount">当前进程句柄或 Linux 文件描述符数量；平台不支持时为空。</param>
/// <param name="UptimeSeconds">Gateway 进程已运行秒数。</param>
public sealed record GatewayProcessSnapshot(
    double CpuPercent,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    long GcCommittedBytes,
    double GcPauseTimePercentage,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ThreadPoolThreads,
    long ThreadPoolPendingWorkItems,
    int ThreadPoolAvailableWorkerThreads,
    int ThreadPoolAvailableIoThreads,
    int? HandleCount,
    long UptimeSeconds);
