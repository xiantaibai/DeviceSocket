using Orleans;
using Orleans.Concurrency;
using DeviceSocket.Cluster.Contracts.Commands;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// 按 DeviceId 激活的设备 Grain，串行管理设备路由、普通发送和可靠消息发送顺序。
/// </summary>
public interface IDeviceGrain : IGrainWithStringKey
{
    /// <summary>注册或替换设备连接，并返回新的 SessionEpoch。</summary>
    Task<DeviceRouteSnapshot> RegisterConnectionAsync(DeviceRouteRegistration registration);

    /// <summary>仅在网关和 Session 仍匹配时刷新活动时间与路由租约。</summary>
    Task<bool> TouchAsync(string gatewayNodeId, string sessionId, DateTimeOffset seenAt, DateTimeOffset leaseExpiresAt);

    /// <summary>仅在网关和 Session 仍匹配时标记设备离线，防止旧连接误删新路由。</summary>
    Task<bool> DisconnectAsync(string gatewayNodeId, string sessionId, DateTimeOffset disconnectedAt);

    /// <summary>取得当前路由；从未登录的设备返回离线空路由。</summary>
    Task<DeviceRouteSnapshot> GetRouteAsync();

    /// <summary>尽力发送普通消息；消息不持久化，失败或设备离线时允许丢弃。</summary>
    Task<BestEffortSendResult> SendBestEffortMessageAsync(BestEffortDeviceMessage request);

    /// <summary>发送可靠消息；消息先由 IDeviceCommandGrain 持久化，再在默认五分钟到期前重试。</summary>
    Task<ReliableMessageReceipt> SendReliableMessageAsync(ReliableDeviceMessage request);

    /// <summary>接收可靠消息的一次后台发送任务；使用单向调用避免 CommandGrain 与 DeviceGrain 循环等待。</summary>
    [OneWay]
    Task DispatchReliableAttemptAsync(ReliableCommandAttempt attempt);
}
