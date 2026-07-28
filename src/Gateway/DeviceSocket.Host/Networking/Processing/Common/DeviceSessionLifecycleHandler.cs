using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Metrics;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 处理所有设备协议共用的 TCP Session 建立和关闭生命周期。
/// </summary>
public sealed class DeviceSessionLifecycleHandler(
    DeviceConnectionCoordinator connectionCoordinator,
    RuntimeMetrics metrics,
    ILogger<DeviceSessionLifecycleHandler> logger)
{
    /// <summary>记录 Session 建立并更新对应协议连接指标。</summary>
    public ValueTask SessionOpenedAsync(IAppSession session, DeviceProtocol protocol)
    {
        metrics.ConnectionOpened(protocol);
        SafeLog(() => logger.LogDebug(
            "TCP session opened: {SessionId}, {Protocol}, {RemoteEndPoint}",
            session.SessionID,
            protocol,
            session.RemoteEndPoint));
        return ValueTask.CompletedTask;
    }

    /// <summary>条件清理 Session 设备绑定，并更新对应协议连接指标。</summary>
    public ValueTask SessionClosedAsync(IAppSession session, DeviceProtocol protocol, string? reason)
    {
        connectionCoordinator.Disconnect(session, DateTimeOffset.UtcNow);
        metrics.ConnectionClosed(protocol);
        SafeLog(() => logger.LogDebug(
            "TCP session closed: {SessionId}, {Protocol}, {Reason}",
            session.SessionID,
            protocol,
            reason));
        return ValueTask.CompletedTask;
    }

    private static void SafeLog(Action writeLog)
    {
        try
        {
            writeLog();
        }
        catch
        {
            // 日志提供程序异常不能反向中断 Session 生命周期。
        }
    }
}
