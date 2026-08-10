namespace DeviceSocket.Host.Configuration.Logging.Messages;

/// <summary>
/// 提供异步日志缓冲丢弃和高水位告警的编译时源生成日志方法。
/// </summary>
internal static partial class LoggingHealthLogMessages
{
    /// <summary>记录指定日志缓冲自上次检查后的新增丢弃数量。</summary>
    [LoggerMessage(
        EventId = 1901,
        EventName = "LogBufferDropped",
        Level = LogLevel.Warning,
        Message = "日志缓冲发生丢弃：LogType={LogType} NewDropped={NewDropped} Dropped={Dropped} Count={Count} BufferSize={BufferSize}")]
    internal static partial void BufferDropped(
        ILogger logger,
        string logType,
        long newDropped,
        long dropped,
        int count,
        int bufferSize);

    /// <summary>记录指定日志缓冲达到百分之八十及以上的高水位。</summary>
    [LoggerMessage(
        EventId = 1902,
        EventName = "LogBufferHighWatermark",
        Level = LogLevel.Warning,
        Message = "日志缓冲达到高水位：LogType={LogType} Count={Count} BufferSize={BufferSize}")]
    internal static partial void BufferHighWatermark(
        ILogger logger,
        string logType,
        int count,
        int bufferSize);
}
