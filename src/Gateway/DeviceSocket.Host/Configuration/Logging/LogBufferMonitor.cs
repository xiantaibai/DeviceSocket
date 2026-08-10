using Serilog.Sinks.Async;

namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// 保存单个 Serilog 异步 Sink 的运行时检查器，并提供无锁缓冲快照。
/// </summary>
public sealed class LogBufferMonitor : IAsyncLogEventSinkMonitor
{
    private readonly string _logType;
    private IAsyncLogEventSinkInspector? _inspector;

    /// <summary>
    /// 使用稳定日志类型创建监控器。
    /// </summary>
    /// <param name="logType">日志流稳定名称。</param>
    public LogBufferMonitor(string logType) => _logType = logType;

    /// <summary>
    /// 获取当前积压、容量和累计丢弃数量的一致性足够的观测快照。
    /// </summary>
    /// <returns>当前异步 Sink 快照。</returns>
    public LogBufferSnapshot Capture()
    {
        var inspector = Volatile.Read(ref _inspector);
        return new LogBufferSnapshot(
            _logType,
            inspector?.Count ?? 0,
            inspector?.BufferSize ?? 0,
            inspector?.DroppedMessagesCount ?? 0);
    }

    /// <summary>
    /// 在异步 Sink 启动时保存检查器引用。
    /// </summary>
    /// <param name="inspector">Serilog 提供的异步缓冲检查器。</param>
    public void StartMonitoring(IAsyncLogEventSinkInspector inspector) =>
        Volatile.Write(ref _inspector, inspector);

    /// <summary>
    /// 仅在停止的是当前 Sink 时清理检查器，避免配置重载覆盖新实例。
    /// </summary>
    /// <param name="inspector">准备停止的异步缓冲检查器。</param>
    public void StopMonitoring(IAsyncLogEventSinkInspector inspector) =>
        Interlocked.CompareExchange(ref _inspector, null, inspector);
}
