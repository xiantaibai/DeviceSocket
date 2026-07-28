using System.Threading.Channels;
using DeviceSocket.Cluster.Contracts.Devices;
using DeviceSocket.Core.Runtime.Connections;
using Microsoft.Extensions.Options;
using Orleans;

namespace DeviceSocket.Host.Networking.Cluster;

/// <summary>
/// 使用有界队列顺序同步设备路由，隔离 Orleans 暂时不可用对 TCP 收包线程的影响。
/// </summary>
public sealed class OrleansDeviceRoutePublisher(
    IClusterClient clusterClient,
    IOnlineDeviceRegistry registry,
    IOptions<GatewayNodeOptions> options,
    ILogger<OrleansDeviceRoutePublisher> logger) : BackgroundService, IDeviceRoutePublisher
{
    private readonly GatewayNodeOptions _options = options.Value;
    private readonly Channel<RouteEvent> _events = Channel.CreateBounded<RouteEvent>(new BoundedChannelOptions(65_536)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    /// <summary>
    /// 尝试排队设备登录事件；不阻塞当前协议处理线程。
    /// </summary>
    public bool TryPublishConnected(DeviceConnectionSnapshot snapshot) =>
        TryWrite(new ConnectedRouteEvent(snapshot));

    /// <summary>
    /// 尝试排队设备心跳事件；队列压力过高时由路由租约兜底失效。
    /// </summary>
    public bool TryPublishActivity(DeviceConnectionSnapshot snapshot, DateTimeOffset seenAt) =>
        TryWrite(new ActivityRouteEvent(snapshot, seenAt));

    /// <summary>
    /// 尝试排队设备断开事件；Orleans 侧还会通过租约处理遗漏的断开通知。
    /// </summary>
    public bool TryPublishDisconnected(DeviceConnectionSnapshot snapshot, DateTimeOffset disconnectedAt) =>
        TryWrite(new DisconnectedRouteEvent(snapshot, disconnectedAt));

    /// <summary>
    /// 单线程消费保证同一设备的登录、心跳和断开事件保持到达顺序。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var routeEvent in _events.Reader.ReadAllAsync(stoppingToken))
            await ProcessWithRetryAsync(routeEvent, stoppingToken);
    }

    /// <summary>
    /// 停止时关闭写端，使消费者能够处理已经进入队列的事件。
    /// </summary>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _events.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private bool TryWrite(RouteEvent routeEvent)
    {
        var accepted = _events.Writer.TryWrite(routeEvent);
        if (!accepted)
            logger.LogWarning("设备路由同步队列已满，事件被拒绝: {EventType} {DeviceId} {SessionId}", routeEvent.GetType().Name, routeEvent.Snapshot.DeviceId, routeEvent.Snapshot.SessionId);
        return accepted;
    }

    private async Task ProcessWithRetryAsync(RouteEvent routeEvent, CancellationToken cancellationToken)
    {
        var delays = new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1) };
        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            try
            {
                if (delays[attempt] > TimeSpan.Zero)
                    await Task.Delay(delays[attempt], cancellationToken);
                await ProcessAsync(routeEvent);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "同步设备路由失败，第 {Attempt} 次: {DeviceId} {SessionId}", attempt + 1, routeEvent.Snapshot.DeviceId, routeEvent.Snapshot.SessionId);
            }
        }

        logger.LogError("同步设备路由达到重试上限: {DeviceId} {SessionId}", routeEvent.Snapshot.DeviceId, routeEvent.Snapshot.SessionId);
    }

    private async Task ProcessAsync(RouteEvent routeEvent)
    {
        var snapshot = routeEvent.Snapshot;
        var grain = clusterClient.GetGrain<IDeviceGrain>(snapshot.DeviceId);
        switch (routeEvent)
        {
            case ConnectedRouteEvent:
                var route = await grain.RegisterConnectionAsync(new DeviceRouteRegistration
                {
                    GatewayNodeId = _options.NodeId,
                    GatewayRpcEndpoint = _options.RpcEndpoint,
                    SessionId = snapshot.SessionId,
                    Protocol = snapshot.Protocol.ToString(),
                    RemoteEndPoint = snapshot.RemoteEndPoint,
                    ConnectedAt = snapshot.ConnectedAt,
                    LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(_options.RouteLeaseSeconds)
                });
                // 只有 Session 仍是本地当前连接时才写回世代号，避免慢响应污染已经重连的设备。
                registry.TryUpdateSessionEpoch(snapshot.DeviceId, snapshot.SessionId, route.SessionEpoch);
                break;

            case ActivityRouteEvent activity:
                await grain.TouchAsync(
                    _options.NodeId,
                    snapshot.SessionId,
                    activity.SeenAt,
                    activity.SeenAt.AddSeconds(_options.RouteLeaseSeconds));
                break;

            case DisconnectedRouteEvent disconnected:
                await grain.DisconnectAsync(_options.NodeId, snapshot.SessionId, disconnected.DisconnectedAt);
                break;
        }
    }

    private abstract record RouteEvent(DeviceConnectionSnapshot Snapshot);
    private sealed record ConnectedRouteEvent(DeviceConnectionSnapshot Snapshot) : RouteEvent(Snapshot);
    private sealed record ActivityRouteEvent(DeviceConnectionSnapshot Snapshot, DateTimeOffset SeenAt) : RouteEvent(Snapshot);
    private sealed record DisconnectedRouteEvent(DeviceConnectionSnapshot Snapshot, DateTimeOffset DisconnectedAt) : RouteEvent(Snapshot);
}
