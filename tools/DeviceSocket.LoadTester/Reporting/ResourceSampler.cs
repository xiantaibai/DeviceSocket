using System.Diagnostics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 采样 LoadTester 当前进程的 CPU、工作集、托管堆、GC、线程池、进程线程和句柄指标，帮助判断负载机是否先于被测服务达到瓶颈。
/// </summary>
internal sealed class ResourceSampler : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private DateTimeOffset _lastAt;
    private TimeSpan _lastCpu;

    /// <summary>读取当前进程初始 CPU 时间，建立后续区间利用率采样基线。</summary>
    internal ResourceSampler()
    {
        _lastAt = DateTimeOffset.UtcNow;
        _lastCpu = _process.TotalProcessorTime;
    }

    /// <summary>采集从上一次调用至今的 CPU 利用率和当前资源仪表。</summary>
    /// <returns>带 UTC 时间戳的负载进程资源快照。</returns>
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
            ThreadPool.PendingWorkItemCount)
        {
            // 线程数与句柄数能直接识别负载机是否发生每设备线程、Socket 或本地资源泄漏；不支持的平台安全回退为 0。
            ProcessThreadCount = TryRead(() => _process.Threads.Count),
            ProcessHandleCount = TryRead(() => _process.HandleCount)
        };
    }

    /// <summary>读取可能因平台或进程刷新竞争失败的本地资源计数。</summary>
    /// <param name="read">实际读取函数。</param>
    /// <returns>读取成功时返回非负值；平台不支持或瞬时失败时返回 0。</returns>
    private static int TryRead(Func<int> read)
    {
        try
        {
            return Math.Max(0, read());
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
        catch (PlatformNotSupportedException)
        {
            return 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 释放用于读取当前进程资源计数器的本地句柄。
    /// </summary>
    public void Dispose() => _process.Dispose();
}
