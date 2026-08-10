using DeviceSocket.Host.Configuration.Logging.Messages;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 作为协议层 Session 看门狗，按固定周期清理登录超时和长期没有合法协议帧的连接。
/// </summary>
/// <remarks>
/// SuperSocket 官方 <c>UseClearIdleSession</c> 负责按底层收发活动清理传输层空闲连接；
/// 本服务补充登录期限和合法协议活动语义，避免垃圾字节或服务端单向发送掩盖设备失联。
/// </remarks>
public sealed class DeviceTcpSessionCleanupWorker(
    DeviceTcpSessionManager sessionManager,
    IOptions<DeviceConnectionOptions> options,
    ILogger<DeviceTcpSessionCleanupWorker> logger) : BackgroundService
{
    private readonly DeviceConnectionOptions _options = options.Value;

    /// <summary>
    /// 由 Generic Host 自动启动，使用单个周期计时器扫描全部 Session，避免为六万连接分别分配计时器和回调任务。
    /// </summary>
    /// <param name="stoppingToken">外层宿主停止时触发的取消令牌。</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.IdleScanIntervalSeconds);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var closed = await sessionManager.CloseExpiredSessionsAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (closed > 0)
                {
                    TcpLogMessages.ExpiredSessionsClosed(
                        logger,
                        closed,
                        sessionManager.LastSessionScanSessions,
                        sessionManager.LastSessionScanCandidates,
                        sessionManager.LastSessionScanDurationMilliseconds);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
