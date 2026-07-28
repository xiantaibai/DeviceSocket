using SuperSocket.Server.Abstractions.Middleware;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 在 SuperSocket 注册 Session 前执行 Gateway 全局连接容量检查。
/// </summary>
public sealed class DeviceConnectionLimitMiddleware : MiddlewareBase
{
    private readonly DeviceConnectionLimiter _connectionLimiter;
    private readonly ILogger<DeviceConnectionLimitMiddleware> _logger;

    /// <summary>
    /// 创建连接限制中间件，并将其放在其他中间件之后，避免后续中间件拒绝导致已占配额无法释放。
    /// </summary>
    public DeviceConnectionLimitMiddleware(
        DeviceConnectionLimiter connectionLimiter,
        ILogger<DeviceConnectionLimitMiddleware> logger)
    {
        _connectionLimiter = connectionLimiter;
        _logger = logger;
        Order = int.MaxValue;
    }

    /// <summary>
    /// 尝试占用一个连接配额；达到上限时返回 false，由 SuperSocket 2.1.0 以 Rejected 原因关闭连接。
    /// </summary>
    /// <param name="session">准备注册到 SuperSocket 的 Session。</param>
    /// <returns>允许继续注册时返回 true；达到容量上限时返回 false。</returns>
    public override ValueTask<bool> RegisterSession(IAppSession session)
    {
        if (_connectionLimiter.TryAcquire(session.SessionID, out var connectionCount, out var rejectedConnections))
            return ValueTask.FromResult(true);

        // 拒绝洪峰期间仅记录前十次和每一千次，避免告警日志反过来造成磁盘压力。
        if (rejectedConnections <= 10 || rejectedConnections % 1_000 == 0)
        {
            SafeLog(() => _logger.LogWarning(
                "TCP connection rejected by middleware because capacity is full: {SessionId}, {RemoteEndPoint}, {ConnectionCount}/{MaxConnections}, rejected total {RejectedConnections}",
                session.SessionID,
                session.RemoteEndPoint,
                connectionCount,
                _connectionLimiter.MaxConnections,
                rejectedConnections));
        }

        return ValueTask.FromResult(false);
    }

    /// <summary>
    /// 在 SuperSocket 注销已接入 Session 时释放对应的全局连接配额。
    /// </summary>
    /// <param name="session">正在注销的 SuperSocket Session。</param>
    /// <returns>中间件注销完成时始终返回 true。</returns>
    public override ValueTask<bool> UnRegisterSession(IAppSession session)
    {
        _connectionLimiter.Release(session.SessionID, out _);
        return ValueTask.FromResult(true);
    }

    private static void SafeLog(Action writeLog)
    {
        try
        {
            writeLog();
        }
        catch
        {
            // 日志故障不能改变中间件对连接容量的判断结果。
        }
    }
}
