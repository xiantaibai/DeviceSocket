namespace DeviceSocket.Host.Configuration.Logging.Messages;

/// <summary>
/// 提供 Gateway 内部 RPC、Orleans 路由和节点租约的编译时源生成日志方法。
/// </summary>
internal static partial class ClusterLogMessages
{
    /// <summary>记录 MagicOnion 设备下发失败。</summary>
    [LoggerMessage(1201, LogLevel.Warning, "MagicOnion 下发失败: {MsgId} {DeviceId} {SessionId}", EventName = "MagicOnionDeviceSendFailed")]
    internal static partial void MagicOnionSendFailed(
        ILogger logger,
        string msgId,
        string deviceId,
        string sessionId,
        Exception exception);

    /// <summary>记录 Gateway 节点租约续租失败。</summary>
    [LoggerMessage(1202, LogLevel.Warning, "网关节点续租失败: {GatewayNodeId}", EventName = "GatewayNodeLeaseFailed")]
    internal static partial void GatewayNodeLeaseFailed(
        ILogger logger,
        string gatewayNodeId,
        Exception exception);

    /// <summary>记录设备路由同步队列已满并拒绝事件。</summary>
    [LoggerMessage(1203, LogLevel.Warning, "设备路由同步队列已满，事件被拒绝: {RouteEventType} {DeviceId} {SessionId}", EventName = "DeviceRouteQueueRejected")]
    internal static partial void RouteQueueRejected(
        ILogger logger,
        string routeEventType,
        string deviceId,
        string sessionId);

    /// <summary>记录设备路由同步单次尝试失败。</summary>
    [LoggerMessage(1204, LogLevel.Warning, "同步设备路由失败，第 {Attempt} 次: {DeviceId} {SessionId}", EventName = "DeviceRouteSyncFailed")]
    internal static partial void RouteSyncFailed(
        ILogger logger,
        int attempt,
        string deviceId,
        string sessionId,
        Exception exception);

    /// <summary>记录设备路由同步达到重试上限。</summary>
    [LoggerMessage(1205, LogLevel.Error, "同步设备路由达到重试上限: {DeviceId} {SessionId}", EventName = "DeviceRouteRetryExhausted")]
    internal static partial void RouteRetryExhausted(
        ILogger logger,
        string deviceId,
        string sessionId);
}
