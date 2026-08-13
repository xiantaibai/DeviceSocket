using Orleans;
using Orleans.Concurrency;
using DeviceSocket.Cluster.Contracts.Commands;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// 按 DeviceId 激活的设备 Grain，串行管理设备路由、普通发送和可靠消息发送顺序。
/// </summary>
public interface IDeviceGrain : IGrainWithStringKey
{
    /// <summary>
    /// 注册或替换设备连接；新 Gateway 或 Session 会递增 SessionEpoch，迟到旧连接不能覆盖新路由。
    /// </summary>
    /// <param name="registration">Gateway 节点、RPC 地址、Session 和路由租约。</param>
    /// <returns>持久化后的当前设备路由。</returns>
    Task<DeviceRouteSnapshot> RegisterConnectionAsync(DeviceRouteRegistration registration);

    /// <summary>
    /// 仅在 Gateway 和 Session 仍匹配时刷新活动时间与路由租约，并允许实现合并高频持久化写入。
    /// </summary>
    /// <param name="gatewayNodeId">上报活动的 Gateway 节点编号。</param>
    /// <param name="sessionId">上报活动的连接 Session。</param>
    /// <param name="seenAt">Gateway 观察到活动的时间。</param>
    /// <param name="leaseExpiresAt">刷新后的路由租约到期时间。</param>
    /// <returns>当前连接匹配时返回 true，旧连接事件返回 false。</returns>
    Task<bool> TouchAsync(string gatewayNodeId, string sessionId, DateTimeOffset seenAt, DateTimeOffset leaseExpiresAt);

    /// <summary>
    /// 仅在 Gateway 和 Session 仍匹配时标记设备离线，防止旧连接关闭事件误删新路由。
    /// </summary>
    /// <param name="gatewayNodeId">关闭连接的 Gateway 节点编号。</param>
    /// <param name="sessionId">关闭的连接 Session。</param>
    /// <param name="disconnectedAt">连接关闭时间。</param>
    /// <returns>当前连接匹配并完成离线更新时返回 true。</returns>
    Task<bool> DisconnectAsync(string gatewayNodeId, string sessionId, DateTimeOffset disconnectedAt);

    /// <summary>
    /// 取得当前路由；从未登录或租约已过期的设备投影为离线。
    /// </summary>
    /// <returns>不暴露 Grain 内部可变状态的路由快照。</returns>
    Task<DeviceRouteSnapshot> GetRouteAsync();

    /// <summary>
    /// 尽力发送非业务控制消息；消息不持久化，失败、离线或可靠命令占用通道时允许丢弃或拒绝。
    /// </summary>
    /// <param name="request">完整协议报文及链路标识。</param>
    /// <returns>本次即时下发结果，不产生自动重试状态。</returns>
    Task<BestEffortSendResult> SendBestEffortMessageAsync(BestEffortDeviceMessage request);

    /// <summary>
    /// 发送可靠命令；命令先由 IDeviceCommandGrain 持久化，再按设备一问一答、回复超时和到期策略执行。
    /// </summary>
    /// <param name="request">全局 MsgId、协议报文、延迟、优先级和重试策略。</param>
    /// <returns>受理结果及提交完成时的命令状态。</returns>
    Task<ReliableMessageReceipt> SendReliableMessageAsync(ReliableDeviceMessage request);

    /// <summary>
    /// 批量受理同一设备的可靠命令；批内统一分配顺序号，设备仍按一问一答逐条执行。
    /// </summary>
    /// <param name="requests">目标均为当前 Grain 设备的可靠命令集合。</param>
    /// <returns>每条命令的受理结果及批量汇总。</returns>
    Task<ReliableMessageBatchReceipt> SendReliableMessagesAsync(ReliableDeviceMessage[] requests);

    /// <summary>
    /// 接收 Gateway 完成协议匹配后的设备回复；旧连接、旧尝试和非当前命令回复会被拒绝。
    /// </summary>
    /// <param name="reply">设备回复及其连接代次。</param>
    /// <returns>回复处理后的可靠命令状态。</returns>
    Task<ReliableMessageReceipt> ReceiveCommandReplyAsync(DeviceCommandReply reply);

    /// <summary>
    /// 取消当前设备的一条可靠命令；若命令正在占用设备通道，则立即释放并继续下一条。
    /// </summary>
    /// <param name="msgId">需要取消的全局命令编号。</param>
    /// <returns>取消后的命令状态；MsgId 不属于当前设备时返回拒绝。</returns>
    Task<ReliableMessageReceipt> CancelReliableMessageAsync(string msgId);

    /// <summary>
    /// 接收分片调度器的到期唤醒；版本不匹配时直接丢弃，避免重复执行。
    /// </summary>
    /// <param name="wakeup">设备调度版本和计划时间。</param>
    [OneWay]
    Task WakeScheduledCommandAsync(DeviceCommandWakeup wakeup);

    /// <summary>
    /// 修复已持久化但 DeviceGrain 尚未确认接管的命令；重复恢复不会重复入队或发送。
    /// </summary>
    /// <param name="recovery">命令编号、设备顺序号和最早发送时间。</param>
    [OneWay]
    Task RecoverPendingCommandAsync(DeviceCommandEnqueueRecovery recovery);

    /// <summary>
    /// 接收兼容旧调用方的一次后台发送任务；只有当前 Active 命令能够执行，避免绕过设备级串行约束。
    /// </summary>
    /// <param name="attempt">CommandGrain 已持久化的尝试编号、动作版本和报文。</param>
    [OneWay]
    Task DispatchReliableAttemptAsync(ReliableCommandAttempt attempt);
}
