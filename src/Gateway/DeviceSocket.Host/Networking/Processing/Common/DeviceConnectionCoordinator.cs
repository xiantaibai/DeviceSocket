using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Connections;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Host.Networking.Cluster;
using DeviceSocket.Host.Networking.Common;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 统一协调本机 Session 注册、活动刷新、离线和 Orleans 路由事件。
/// </summary>
public sealed class DeviceConnectionCoordinator(
    IOnlineDeviceRegistry registry,
    IDeviceRoutePublisher routePublisher,
    IDeviceTrafficSink trafficSink)
{
    /// <summary>
    /// 注册明确登录的设备；新 Session 才发布集群上线事件。
    /// </summary>
    public DeviceRegistration Register(
        IAppSession session,
        DeviceProtocol protocol,
        string deviceId,
        DateTimeOffset now)
    {
        var registration = registry.Register(
            deviceId,
            new SuperSocketDeviceConnection(session, protocol, deviceId, trafficSink),
            now);
        if (registration.IsNewSession)
            routePublisher.TryPublishConnected(registration.Current);
        return registration;
    }

    /// <summary>
    /// 注册无独立登录命令的设备；已绑定 Session 则发布活动事件。
    /// </summary>
    public DeviceRegistration RegisterOrRefresh(
        IAppSession session,
        DeviceProtocol protocol,
        string deviceId,
        DateTimeOffset now)
    {
        var registration = Register(session, protocol, deviceId, now);
        if (!registration.IsNewSession)
            routePublisher.TryPublishActivity(registration.Current, now);
        return registration;
    }

    /// <summary>
    /// 仅在设备仍属于当前 Session 时刷新活动时间并发布路由续租。
    /// </summary>
    public bool Touch(IAppSession session, string deviceId, DateTimeOffset now)
    {
        if (!registry.Touch(deviceId, session.SessionID, now) ||
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
    public bool Disconnect(IAppSession session, DateTimeOffset now)
    {
        if (!registry.RemoveSession(session.SessionID, out var snapshot) || snapshot is null)
            return false;

        routePublisher.TryPublishDisconnected(snapshot, now);
        return true;
    }
}
