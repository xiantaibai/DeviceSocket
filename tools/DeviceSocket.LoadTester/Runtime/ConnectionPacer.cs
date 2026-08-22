using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 在所有虚拟设备之间共享连接时间槽，按配置的每秒连接数平滑发放许可并记录调度滞后，避免线程批量唤醒形成非预期尖峰。
/// </summary>
/// <param name="connectionsPerSecond">当前分片的初始连接速率，单位连接数/秒；允许小数以精确分摊低于分片数的全局速率，运行阶段可动态覆盖。</param>
/// <param name="metrics">接收连接调度滞后直方图的本节点指标容器。</param>
internal sealed class ConnectionPacer(double connectionsPerSecond, LoadTestMetrics metrics) : IDisposable
{
    private readonly SemaphoreSlim _scheduleLock = new(1, 1);
    private DateTimeOffset _nextSlotUtc = DateTimeOffset.MinValue;
    private double _connectionsPerSecond = connectionsPerSecond;

    /// <summary>
    /// 更新当前分片的连接速率；零表示 Burst 模式下不做时间槽限速。
    /// </summary>
    /// <param name="connectionsPerSecond">当前分片连接数/秒，必须由全局速率除以分片数得到。</param>
    internal void UpdateRate(double connectionsPerSecond) =>
        Interlocked.Exchange(ref _connectionsPerSecond, connectionsPerSecond);

    internal async Task WaitAsync(CancellationToken cancellationToken)
    {
        var rate = Volatile.Read(ref _connectionsPerSecond);
        if (rate <= 0)
            return;

        DateTimeOffset scheduledAt;
        await _scheduleLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            scheduledAt = _nextSlotUtc > now ? _nextSlotUtc : now;
            _nextSlotUtc = scheduledAt.AddSeconds(1d / rate);
        }
        finally
        {
            _scheduleLock.Release();
        }

        var delay = scheduledAt - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);
        metrics.ConnectionScheduleLag(DateTimeOffset.UtcNow - scheduledAt);
    }

    void IDisposable.Dispose() => _scheduleLock.Dispose();
}
