using Microsoft.Extensions.Options;

namespace DeviceSocket.Host.Networking.QGdw3761.Commands;

/// <summary>
/// 以节点级单一周期任务清理整体到期的 376.1 命令关联，避免为数万 Session 创建独立 Timer。
/// </summary>
public sealed class QGdw3761CommandCorrelationCleanupWorker(
    QGdw3761CommandCorrelationRegistry registry,
    IOptions<QGdw3761CommandCorrelationOptions> options) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(options.Value.CleanupIntervalSeconds);

    /// <summary>
    /// 按固定整数秒周期执行有界内存清理，应用停止时立即退出。
    /// </summary>
    /// <param name="stoppingToken">宿主停止令牌。</param>
    /// <returns>后台清理循环任务。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            registry.RemoveExpired(DateTimeOffset.UtcNow);
    }
}
