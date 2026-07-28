namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// Gateway 程序日志和 Command 通信日志的文件、保留与异步缓冲配置。
/// </summary>
public sealed class DeviceLoggingOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "DeviceLogging";

    /// <summary>程序日志滚动文件路径。</summary>
    public string ApplicationPath { get; set; } = "logs/app/app-.jsonl";

    /// <summary>Command 通信日志滚动文件路径。</summary>
    public string CommandPath { get; set; } = "logs/command/command-.jsonl";

    /// <summary>单个日志文件的最大字节数。</summary>
    public long FileSizeLimitBytes { get; set; } = 268_435_456;

    /// <summary>每类日志最多保留的滚动文件数量。</summary>
    public int RetainedFileCount { get; set; } = 31;

    /// <summary>滚动日志最多保留的天数。</summary>
    public int RetainedDays { get; set; } = 30;

    /// <summary>程序日志异步缓冲容量。</summary>
    public int ApplicationBufferSize { get; set; } = 10_000;

    /// <summary>Command 通信日志异步缓冲容量。</summary>
    public int CommandBufferSize { get; set; } = 100_000;

    /// <summary>日志文件强制刷新到磁盘的间隔秒数。</summary>
    public int FlushIntervalSeconds { get; set; } = 1;

    /// <summary>Command 异步缓冲健康检查间隔秒数。</summary>
    public int MonitorIntervalSeconds { get; set; } = 10;
}
