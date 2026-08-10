using System.Collections.Concurrent;

namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// 集中维护程序、设备上行和设备下发异步日志流的监控器。
/// </summary>
public sealed class LogBufferMonitorRegistry
{
    /// <summary>普通程序日志流名称。</summary>
    public const string ApplicationLogType = "Application";

    private readonly ConcurrentDictionary<string, LogBufferMonitor> _monitors =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 获取或创建指定日志流的唯一异步缓冲监控器。
    /// </summary>
    /// <param name="logType">日志流稳定名称。</param>
    /// <returns>可交给 Serilog 异步 Sink 的监控器。</returns>
    public LogBufferMonitor GetOrCreate(string logType) =>
        _monitors.GetOrAdd(logType, static value => new LogBufferMonitor(value));

    /// <summary>
    /// 按日志类型排序返回全部已注册异步缓冲快照。
    /// </summary>
    /// <returns>当前日志缓冲快照集合。</returns>
    public IReadOnlyList<LogBufferSnapshot> CaptureAll() =>
        _monitors.Values
            .Select(static monitor => monitor.Capture())
            .OrderBy(static snapshot => snapshot.LogType, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// 获取指定日志流快照；尚未创建对应 Sink 时返回容量为零的快照。
    /// </summary>
    /// <param name="logType">日志流稳定名称。</param>
    /// <returns>指定日志流当前快照。</returns>
    public LogBufferSnapshot Capture(string logType) =>
        _monitors.TryGetValue(logType, out var monitor)
            ? monitor.Capture()
            : new LogBufferSnapshot(logType, 0, 0, 0);
}
