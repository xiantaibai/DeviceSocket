using System.Diagnostics;

namespace DeviceSocket.LoadTester.Reporting;

internal sealed class ResourceSampler : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private DateTimeOffset _lastAt;
    private TimeSpan _lastCpu;

    internal ResourceSampler()
    {
        _lastAt = DateTimeOffset.UtcNow;
        _lastCpu = _process.TotalProcessorTime;
    }

    internal ResourceSnapshot Sample()
    {
        _process.Refresh();
        var now = DateTimeOffset.UtcNow;
        var cpu = _process.TotalProcessorTime;
        var elapsed = now - _lastAt;
        var cpuPercent = elapsed > TimeSpan.Zero
            ? (cpu - _lastCpu).TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100
            : 0;
        _lastAt = now;
        _lastCpu = cpu;

        return new ResourceSnapshot(
            now,
            Math.Max(0, cpuPercent),
            _process.WorkingSet64,
            GC.GetTotalMemory(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount);
    }

    /// <summary>
    /// 释放用于读取当前进程资源计数器的本地句柄。
    /// </summary>
    public void Dispose() => _process.Dispose();
}
