using DeviceSocket.Host.Configuration.Logging.Messages;
using SuperSocket.Server.Abstractions.Middleware;
using SuperSocket.Server.Abstractions.Session;
using System.Net;

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
    /// <param name="connectionLimiter">两个设备 TCP 子宿主共用的全局连接配额管理器。</param>
    /// <param name="logger">记录采样拒绝告警的程序日志器。</param>
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
        // 单 IP 限制默认关闭以兼容运营商 NAT；关闭时不调用 IPAddress.ToString，避免为每条长连接产生无效字符串。
        var remoteAddress = _connectionLimiter.PerIpLimitEnabled &&
                            session.RemoteEndPoint is IPEndPoint endpoint
            ? NormalizeAddress(endpoint.Address)
            : null;
        if (_connectionLimiter.TryAcquire(
                session.SessionID,
                remoteAddress,
                DateTimeOffset.UtcNow,
                out var connectionCount,
                out var rejectedConnections,
                out var rejectionReason))
        {
            return ValueTask.FromResult(true);
        }

        // 拒绝洪峰期间仅记录前十次和每一千次，避免告警日志反过来造成磁盘压力。
        if (rejectedConnections <= 10 || rejectedConnections % 1_000 == 0)
        {
            try
            {
                TcpLogMessages.ConnectionRejected(
                    _logger,
                    rejectionReason,
                    session.SessionID,
                    session.RemoteEndPoint?.ToString(),
                    connectionCount,
                    _connectionLimiter.MaxConnections,
                    rejectedConnections);
            }
            catch
            {
                // 日志故障不能改变中间件对连接容量的判断结果。
            }
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

    /// <summary>
    /// 把 IPv4 映射的 IPv6 地址还原为 IPv4 文本，确保同一远端只占用一个统计键。
    /// </summary>
    /// <param name="address">Socket 报告的远端 IP 地址。</param>
    /// <returns>用于单 IP 配额字典的归一化文本。</returns>
    private static string NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
}
