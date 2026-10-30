namespace DeviceSocket.LoadTester.Runtime;

/// <summary>复用会话现有唤醒点；更早的业务回复到期时取消旧等待，任何时刻最多保留一个计时等待。</summary>
internal sealed class SessionWakeupTimer : IDisposable
{
    private readonly CancellationToken _sessionToken;
    private CancellationTokenSource? _current;
    private DateTimeOffset _due;
    private Task? _task;

    /// <summary>绑定所属连接生命周期，断线时终止等待。</summary>
    /// <param name="sessionToken">当前连接取消令牌。</param>
    internal SessionWakeupTimer(CancellationToken sessionToken) => _sessionToken = sessionToken;

    /// <summary>等待下一次心跳、上报或应答到期，未变化且尚未完成时复用原等待。</summary>
    /// <param name="due">最近到期 UTC 时间；MaxValue 表示只等待取消。</param>
    /// <returns>可与 Socket 读取一起等待的任务。</returns>
    internal Task WaitUntil(DateTimeOffset due)
    {
        if (_task is not null && !_task.IsCompleted && due == _due)
            return _task;
        _current?.Cancel();
        _current?.Dispose();
        _current = CancellationTokenSource.CreateLinkedTokenSource(_sessionToken);
        _due = due;
        var delay = due == DateTimeOffset.MaxValue ? Timeout.InfiniteTimeSpan : due - DateTimeOffset.UtcNow;
        if (due != DateTimeOffset.MaxValue && delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        return _task = Task.Delay(delay, _current.Token);
    }

    /// <summary>释放当前等待，避免接收密集时遗留大量不再使用的定时任务。</summary>
    public void Dispose() { _current?.Cancel(); _current?.Dispose(); }
}
