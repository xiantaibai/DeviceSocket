using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Metrics;
using DeviceSocket.Host.Configuration.Logging.Messages;
using DeviceSocket.Host.Networking.Common;
using SuperSocket.Connection;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 处理所有设备协议共用的 TCP Session 建立和关闭生命周期。
/// </summary>
/// <param name="connectionCoordinator">负责设备绑定和断开清理的连接协调器。</param>
/// <param name="sessionManager">统一跟踪设备 TCP Session 的管理器。</param>
/// <param name="metrics">Gateway 连接运行指标。</param>
/// <param name="logger">记录生命周期异常和调试信息的程序日志器。</param>
public sealed class DeviceSessionLifecycleHandler(
    DeviceConnectionCoordinator connectionCoordinator,
    DeviceTcpSessionManager sessionManager,
    RuntimeMetrics metrics,
    ILogger<DeviceSessionLifecycleHandler> logger)
{
    /// <summary>记录通过中间件容量检查的 Session；注册异常时立即关闭，避免连接脱离协议层看门狗。</summary>
    /// <param name="session">刚完成 SuperSocket 注册的 Session。</param>
    /// <param name="protocol">当前监听端口对应的设备协议。</param>
    /// <returns>Session 登记或异常关闭完成后的异步结果。</returns>
    public async ValueTask SessionOpenedAsync(IAppSession session, DeviceProtocol protocol)
    {
        if (!sessionManager.RegisterSession(
                new SuperSocketSessionControl(session),
                protocol,
                DateTimeOffset.UtcNow))
        {
            try
            {
                TcpLogMessages.SessionRegistrationConflict(
                    logger,
                    session.SessionID,
                    protocol,
                    session.RemoteEndPoint?.ToString());
            }
            catch
            {
                // 日志提供程序异常不能阻止未被看门狗跟踪的冲突 Session 执行关闭。
            }
            await session.CloseAsync(CloseReason.ApplicationError);
            return;
        }

        metrics.ConnectionOpened(protocol);
        try
        {
            TcpLogMessages.SessionOpened(
                logger,
                session.SessionID,
                protocol,
                session.RemoteEndPoint?.ToString());
        }
        catch
        {
            // 日志提供程序异常不能反向中断 Session 打开生命周期。
        }
    }

    /// <summary>清理 Session 设备绑定，并更新对应协议连接指标和官方空闲关闭计数。</summary>
    /// <param name="session">已经关闭的 SuperSocket Session。</param>
    /// <param name="protocol">当前监听端口对应的设备协议。</param>
    /// <param name="reason">SuperSocket 报告的底层关闭原因。</param>
    /// <returns>生命周期清理完成后的已完成任务。</returns>
    public ValueTask SessionClosedAsync(IAppSession session, DeviceProtocol protocol, CloseReason reason)
    {
        // 业务主动关闭会先把 Session 标记为 Closing 并自行计数，只有官方中间件直接触发的 TimeOut 才在此补记。
        if (reason == CloseReason.TimeOut)
            sessionManager.RecordSuperSocketIdleTimeout(session.SessionID);
        sessionManager.UnregisterSession(session.SessionID);
        connectionCoordinator.Disconnect(session.SessionID, DateTimeOffset.UtcNow);
        metrics.ConnectionClosed(protocol);
        try
        {
            TcpLogMessages.SessionClosed(logger, session.SessionID, protocol, reason);
        }
        catch
        {
            // 日志提供程序异常不能反向中断 Session 关闭生命周期。
        }
        return ValueTask.CompletedTask;
    }
}
