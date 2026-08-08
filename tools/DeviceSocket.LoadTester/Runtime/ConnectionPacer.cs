using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Runtime;

internal sealed class ConnectionPacer(int connectionsPerSecond, LoadTestMetrics metrics) : IDisposable
{
    private readonly SemaphoreSlim _scheduleLock = new(1, 1);
    private DateTimeOffset _nextSlotUtc = DateTimeOffset.MinValue;
    private int _connectionsPerSecond = connectionsPerSecond;

    internal void UpdateRate(int connectionsPerSecond) =>
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
