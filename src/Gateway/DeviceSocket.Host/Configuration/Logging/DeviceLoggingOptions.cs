namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// Gateway 程序日志、设备上下行日志、心跳策略、文件保留与异步缓冲配置。
/// </summary>
public sealed class DeviceLoggingOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "DeviceLogging";

    /// <summary>程序日志滚动文件路径；默认 `logs/app/app-.jsonl`，相对路径以 Gateway 内容根目录解析。</summary>
    public string ApplicationPath { get; set; } = "logs/app/app-.jsonl";

    /// <summary>设备上行通信日志滚动文件路径；默认 `logs/device/upstream-.jsonl`，可配置为数据盘绝对路径。</summary>
    public string UpstreamPath { get; set; } = "logs/device/upstream-.jsonl";

    /// <summary>设备下发通信日志滚动文件路径；默认 `logs/device/downstream-.jsonl`，可配置为数据盘绝对路径。</summary>
    public string DownstreamPath { get; set; } = "logs/device/downstream-.jsonl";

    /// <summary>单个滚动日志文件的字节上限；默认 268,435,456 字节（256 MiB），必须大于零。</summary>
    public long FileSizeLimitBytes { get; set; } = 268_435_456;

    /// <summary>每条日志流最多保留的滚动文件数量；默认 31 个，必须大于零。</summary>
    public int RetainedFileCount { get; set; } = 31;

    /// <summary>每条日志流滚动文件的最长保留天数；默认 30 天，文件数和天数任一条件满足即可清理。</summary>
    public int RetainedDays { get; set; } = 30;

    /// <summary>程序日志异步缓冲消息数；默认 10,000 条，满时丢弃而不阻塞业务线程。</summary>
    public int ApplicationBufferSize { get; set; } = 10_000;

    /// <summary>设备上行日志异步缓冲消息数；默认 50,000 条，满时增加丢弃指标。</summary>
    public int UpstreamBufferSize { get; set; } = 50_000;

    /// <summary>设备下发日志异步缓冲消息数；默认 50,000 条，满时增加丢弃指标。</summary>
    public int DownstreamBufferSize { get; set; } = 50_000;

    /// <summary>日志文件 Sink 刷新到磁盘的周期秒数；默认 1 秒，必须大于零。</summary>
    public int FlushIntervalSeconds { get; set; } = 1;

    /// <summary>全部异步日志缓冲健康检查周期秒数；默认 10 秒，必须大于零。</summary>
    public int MonitorIntervalSeconds { get; set; } = 10;

    /// <summary>未命中指定设备规则时的心跳设备采样比例；默认 0，取值 0～1，0 表示完全不记录、1 表示全部记录。</summary>
    public double HeartbeatSampleRate { get; set; }

    /// <summary>始终记录上行心跳及对应下行确认的设备编号列表；默认空数组，配置文件重新加载后无需重启即可生效。</summary>
    public string[] HeartbeatEnabledDeviceIds { get; set; } = [];
}
