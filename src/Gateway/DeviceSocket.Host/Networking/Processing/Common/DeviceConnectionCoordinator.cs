using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Connections;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Host.Networking.Cluster;
using DeviceSocket.Host.Networking.Common;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 统一协调本机 Session 认证、设备注册、重复登录关闭、活动刷新、离线和 Orleans 路由事件。
/// </summary>
public sealed class DeviceConnectionCoordinator(
    IOnlineDeviceRegistry registry,
    IDeviceRoutePublisher routePublisher,
    IDeviceTrafficSink trafficSink,
    DeviceTcpSessionManager sessionManager)
{
    /// <summary>
    /// 注册明确登录的设备；只有仍存活的 Session 才能进入在线表，新 Session 会主动关闭被替换的旧连接。
    /// </summary>
    /// <param name="sessionId">当前 SuperSocket Session 编号。</param>
    /// <param name="remoteEndPoint">当前连接的远端端点文本。</param>
    /// <param name="protocol">当前监听端口对应的设备协议。</param>
    /// <param name="deviceId">协议帧解析出的设备唯一编号。</param>
    /// <param name="now">登录报文接收时间。</param>
    /// <returns>注册成功时返回当前注册结果；Session 已经关闭时返回空。</returns>
    public async ValueTask<DeviceRegistration?> RegisterAsync(
        string sessionId,
        string? remoteEndPoint,
        DeviceProtocol protocol,
        string deviceId,
        DateTimeOffset now)
    {
        // 先确认 Session 仍在 TCP 管理器中；若关闭事件与登录并发，后续二次检查会撤销可能产生的在线表残留。
        if (!sessionManager.TryAuthenticate(sessionId, deviceId, now))
        {
            // 同一 TCP Session 改绑到另一设备会破坏 Session 到设备的一对一索引，因此按失效连接关闭。
            await sessionManager.CloseSessionAsync(sessionId, DeviceSessionCloseReason.StaleSession);
            return null;
        }

        var registration = registry.Register(
            deviceId,
            new SuperSocketDeviceConnection(
                sessionId,
                protocol,
                deviceId,
                remoteEndPoint,
                sessionManager,
                trafficSink),
            now);
        if (!sessionManager.IsActive(sessionId))
        {
            registry.RemoveSession(sessionId);
            return null;
        }

        if (registration.IsNewSession)
            routePublisher.TryPublishConnected(registration.Current);
        if (registration.ReplacedConnection is { } replaced)
        {
            // 在线表已经切换到新 Session，旧连接的迟到关闭事件会被条件删除保护，不会误删新路由。
            await sessionManager.CloseSessionAsync(
                replaced.SessionId,
                DeviceSessionCloseReason.DuplicateLogin);
        }

        return registration;
    }

    /// <summary>
    /// 注册无独立登录命令的设备；已绑定 Session 则发布活动事件。
    /// </summary>
    /// <param name="sessionId">当前 SuperSocket Session 编号。</param>
    /// <param name="remoteEndPoint">当前连接的远端端点文本。</param>
    /// <param name="protocol">当前监听端口对应的设备协议。</param>
    /// <param name="deviceId">协议帧解析出的设备唯一编号。</param>
    /// <param name="now">合法设备帧接收时间。</param>
    /// <returns>注册成功时返回当前注册结果；Session 已经关闭时返回空。</returns>
    public async ValueTask<DeviceRegistration?> RegisterOrRefreshAsync(
        string sessionId,
        string? remoteEndPoint,
        DeviceProtocol protocol,
        string deviceId,
        DateTimeOffset now)
    {
        var registration = await RegisterAsync(sessionId, remoteEndPoint, protocol, deviceId, now);
        if (registration is { IsNewSession: false })
            routePublisher.TryPublishActivity(registration.Current, now);
        return registration;
    }

    /// <summary>
    /// 仅在设备仍属于当前 Session 时刷新活动时间并发布路由续租。
    /// </summary>
    /// <param name="sessionId">发送活动报文的 Session 编号。</param>
    /// <param name="deviceId">报文携带的设备编号。</param>
    /// <param name="now">合法报文接收时间。</param>
    /// <returns>当前 Session 仍是设备在线连接时返回 true。</returns>
    public bool Touch(string sessionId, string deviceId, DateTimeOffset now)
    {
        if (!registry.Touch(deviceId, sessionId, now) ||
            !registry.TryGetSnapshot(deviceId, out var snapshot) ||
            snapshot is null)
        {
            return false;
        }

        routePublisher.TryPublishActivity(snapshot, now);
        return true;
    }

    /// <summary>
    /// 按 SessionId 条件移除本机连接，并将真正被删除的路由发布为离线。
    /// </summary>
    /// <param name="sessionId">已经断开的 Session 编号。</param>
    /// <param name="now">断开时间。</param>
    /// <returns>本次是否实际删除了在线设备绑定。</returns>
    public bool Disconnect(string sessionId, DateTimeOffset now)
    {
        if (!registry.RemoveSession(sessionId, out var snapshot) || snapshot is null)
            return false;

        routePublisher.TryPublishDisconnected(snapshot, now);
        return true;
    }

    /// <summary>
    /// 在完成必要协议响应后主动关闭指定 Session，实际离线清理由 SessionClosed 回调执行。
    /// </summary>
    /// <param name="sessionId">准备关闭的 Session 编号。</param>
    /// <param name="reason">主动关闭的业务原因。</param>
    /// <returns>本次是否首次发起关闭。</returns>
    public ValueTask<bool> CloseSessionAsync(string sessionId, DeviceSessionCloseReason reason) =>
        sessionManager.CloseSessionAsync(sessionId, reason);
}
