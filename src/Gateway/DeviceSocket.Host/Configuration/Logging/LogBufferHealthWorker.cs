using DeviceSocket.Host.Configuration.Logging.Messages;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// 定期监控全部异步日志缓冲，并把新增丢弃和高水位写入程序告警日志。
/// </summary>
public sealed class LogBufferHealthWorker(
    LogBufferMonitorRegistry registry,
    IOptions<DeviceLoggingOptions> options,
    ILogger<LogBufferHealthWorker> logger) : BackgroundService
{
    private readonly DeviceLoggingOptions _options = options.Value;
    private readonly Dictionary<string, long> _reportedDroppedCounts = new(StringComparer.Ordinal);

    /// <summary>
    /// 按配置周期检查日志缓冲；健康告警只进入程序日志，不能流回设备通讯日志。
    /// </summary>
    /// <param name="stoppingToken">宿主停止令牌。</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_options.MonitorIntervalSeconds, 1));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                InspectBuffers();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 检查每条日志流的缓冲水位和累计丢弃数，只报告新增异常以避免重复告警。
    /// </summary>
    private void InspectBuffers()
    {
        foreach (var snapshot in registry.CaptureAll())
        {
            var reported = _reportedDroppedCounts.GetValueOrDefault(snapshot.LogType);
            if (snapshot.DroppedMessagesCount > reported)
            {
                LoggingHealthLogMessages.BufferDropped(
                    logger,
                    snapshot.LogType,
                    snapshot.DroppedMessagesCount - reported,
                    snapshot.DroppedMessagesCount,
                    snapshot.Count,
                    snapshot.BufferSize);
                _reportedDroppedCounts[snapshot.LogType] = snapshot.DroppedMessagesCount;
            }
            else if (snapshot.BufferSize > 0 && snapshot.Count * 100L / snapshot.BufferSize >= 80)
            {
                LoggingHealthLogMessages.BufferHighWatermark(
                    logger,
                    snapshot.LogType,
                    snapshot.Count,
                    snapshot.BufferSize);
            }
        }
    }
}
