using System.Diagnostics;

namespace DeviceSocket.Host.Observability;

/// <summary>
/// 线程安全采样 Gateway 当前进程资源，供状态接口和 Linux 长稳压测读取。
/// </summary>
public sealed class GatewayProcessMetrics : IDisposable
{
    private readonly object _gate = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastSampleAt;
    private TimeSpan _lastProcessorTime;

    /// <summary>
    /// 创建进程采样器并保存首个 CPU 计算基线。
    /// </summary>
    public GatewayProcessMetrics()
    {
        _lastSampleAt = DateTimeOffset.UtcNow;
        _lastProcessorTime = _process.TotalProcessorTime;
    }

    /// <summary>
    /// 获取当前进程资源快照；CPU 使用率由相邻两次调用的处理器时间差计算。
    /// </summary>
    /// <returns>当前进程资源快照。</returns>
    public GatewayProcessSnapshot Capture()
    {
        lock (_gate)
        {
            _process.Refresh();
            var now = DateTimeOffset.UtcNow;
            var processorTime = _process.TotalProcessorTime;
            var elapsed = now - _lastSampleAt;
            var cpuPercent = elapsed > TimeSpan.Zero
                ? (processorTime - _lastProcessorTime).TotalMilliseconds /
                  elapsed.TotalMilliseconds /
                  Environment.ProcessorCount * 100
                : 0;
            _lastSampleAt = now;
            _lastProcessorTime = processorTime;

            var gcInfo = GC.GetGCMemoryInfo();
            ThreadPool.GetAvailableThreads(out var availableWorkerThreads, out var availableIoThreads);
            return new GatewayProcessSnapshot(
                Math.Max(0, cpuPercent),
                _process.WorkingSet64,
                GC.GetTotalMemory(false),
                gcInfo.TotalCommittedBytes,
                gcInfo.PauseTimePercentage,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                ThreadPool.ThreadCount,
                ThreadPool.PendingWorkItemCount,
                availableWorkerThreads,
                availableIoThreads,
                TryGetHandleCount(),
                Math.Max(0, (long)(now - _startedAt).TotalSeconds));
        }
    }

    /// <summary>释放用于读取当前进程指标的本地句柄。</summary>
    public void Dispose() => _process.Dispose();

    /// <summary>
    /// 尝试读取当前进程句柄数；运行平台不支持或进程已退出时返回空值。
    /// </summary>
    /// <returns>当前进程句柄数，无法读取时返回 null。</returns>
    private int? TryGetHandleCount()
    {
        try
        {
            return _process.HandleCount;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
