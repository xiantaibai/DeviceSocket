using DeviceSocket.Cluster.Contracts.Scheduling;
using DeviceSocket.Cluster.Grains.Scheduling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Cluster.Silo.Scheduling;

/// <summary>
/// 宿主启动后幂等激活固定数量的 SchedulerGrain，确保新数据库尚无 Reminder 时也能开始恢复扫描。
/// </summary>
public sealed partial class DeviceCommandSchedulerBootstrapService(
    IGrainFactory grainFactory,
    IOptions<DeviceCommandSchedulingOptions> options,
    IHostApplicationLifetime applicationLifetime,
    ILogger<DeviceCommandSchedulerBootstrapService> logger)
    : BackgroundService
{
    /// <summary>
    /// 等待宿主完全启动后分批激活调度分片；失败时退避重试，直到成功或宿主停止。
    /// </summary>
    /// <param name="stoppingToken">宿主停止信号。</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForApplicationStartedAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BootstrapAsync(stoppingToken);
                LogBootstrapCompleted(logger, options.Value.ShardCount);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogBootstrapFailed(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    /// <summary>
    /// 以固定并发批次激活全部调度分片，避免多个 Silo 同时启动时产生请求尖峰。
    /// </summary>
    /// <param name="cancellationToken">宿主停止信号。</param>
    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        const int concurrency = 32;
        for (var start = 0; start < options.Value.ShardCount; start += concurrency)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(concurrency, options.Value.ShardCount - start);
            var tasks = new Task[count];
            for (var offset = 0; offset < count; offset++)
            {
                var shardId = start + offset;
                tasks[offset] = grainFactory
                    .GetGrain<IDeviceCommandSchedulerGrain>(shardId)
                    .EnsureStartedAsync();
            }

            await Task.WhenAll(tasks);
        }
    }

    /// <summary>
    /// 把 ApplicationStarted 回调转换为可取消任务，避免在 Orleans Runtime 就绪前调用 Grain。
    /// </summary>
    /// <param name="cancellationToken">宿主停止信号。</param>
    private Task WaitForApplicationStartedAsync(CancellationToken cancellationToken)
    {
        if (applicationLifetime.ApplicationStarted.IsCancellationRequested)
            return Task.CompletedTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        applicationLifetime.ApplicationStarted.Register(completion.SetResult);
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
    }

    [LoggerMessage(2201, LogLevel.Information, "已完成 {ShardCount} 个命令调度分片的幂等启动")]
    private static partial void LogBootstrapCompleted(ILogger logger, int shardCount);

    [LoggerMessage(2202, LogLevel.Warning, "命令调度分片启动失败，10 秒后重试")]
    private static partial void LogBootstrapFailed(ILogger logger, Exception exception);
}
