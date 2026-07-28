using Serilog.Sinks.Async;

namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// 保存 Serilog Command 异步 Sink 的运行时检查器，供健康监控读取积压和丢弃数量。
/// </summary>
public sealed class CommandLogAsyncMonitor : IAsyncLogEventSinkMonitor
{
    private IAsyncLogEventSinkInspector? _inspector;

    /// <summary>取得当前缓冲中等待写入的日志数量。</summary>
    public int Count => Volatile.Read(ref _inspector)?.Count ?? 0;

    /// <summary>取得异步缓冲总容量。</summary>
    public int BufferSize => Volatile.Read(ref _inspector)?.BufferSize ?? 0;

    /// <summary>取得因缓冲溢出而丢弃的累计 Command 日志数量。</summary>
    public long DroppedMessagesCount => Volatile.Read(ref _inspector)?.DroppedMessagesCount ?? 0;

    /// <summary>在异步 Sink 启动时保存检查器引用。</summary>
    public void StartMonitoring(IAsyncLogEventSinkInspector inspector) =>
        Volatile.Write(ref _inspector, inspector);

    /// <summary>仅在停止的正是当前 Sink 时清理检查器。</summary>
    public void StopMonitoring(IAsyncLogEventSinkInspector inspector) =>
        Interlocked.CompareExchange(ref _inspector, null, inspector);
}
