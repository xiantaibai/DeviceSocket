using Microsoft.Extensions.Options;

namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// 定期监控 Command 日志缓冲，对新增丢弃和高水位写入程序告警日志。
/// </summary>
public sealed class CommandLogHealthWorker(
    CommandLogAsyncMonitor monitor,
    IOptions<DeviceLoggingOptions> options,
    ILogger<CommandLogHealthWorker> logger) : BackgroundService
{
    private readonly DeviceLoggingOptions _options = options.Value;
    private long _reportedDroppedCount;

    /// <summary>
    /// 按配置周期检查异步缓冲；监控日志不写入 Command Sink，避免自循环。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_options.MonitorIntervalSeconds, 1));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var dropped = monitor.DroppedMessagesCount;
            if (dropped > _reportedDroppedCount)
            {
                logger.LogWarning(
                    "Command 日志缓冲已丢弃 {NewDropped} 条记录，累计 {Dropped}，当前积压 {Count}/{BufferSize}。",
                    dropped - _reportedDroppedCount,
                    dropped,
                    monitor.Count,
                    monitor.BufferSize);
                _reportedDroppedCount = dropped;
            }
            else if (monitor.BufferSize > 0 && monitor.Count * 100 / monitor.BufferSize >= 80)
            {
                logger.LogWarning(
                    "Command 日志缓冲高水位: {Count}/{BufferSize}。",
                    monitor.Count,
                    monitor.BufferSize);
            }
        }
    }
}
