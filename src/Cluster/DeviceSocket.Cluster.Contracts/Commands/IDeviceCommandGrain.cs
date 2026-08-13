using Orleans;

namespace DeviceSocket.Cluster.Contracts.Commands;

/// <summary>
/// 按 MsgId 激活的可靠消息 Grain，独立管理一条重要消息的持久化、重试、到期和确认。
/// </summary>
public interface IDeviceCommandGrain : IGrainWithStringKey
{
    /// <summary>
    /// 幂等创建命令，并检查相同 MsgId 的设备、协议、报文和执行策略是否一致。
    /// </summary>
    /// <param name="registration">需要首次持久化的命令内容和设备顺序号。</param>
    /// <returns>是否首次创建、内容是否一致及当前快照。</returns>
    /// <exception cref="ArgumentException">必填标识或报文为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">时间、超时、序号或尝试次数无效。</exception>
    Task<ReliableCommandCreateResult> CreateAsync(ReliableCommandRegistration registration);

    /// <summary>
    /// 标记设备执行游标已经接管命令；重复调用保持幂等，序号不一致时返回当前快照而不覆盖状态。
    /// </summary>
    /// <param name="deviceSequence">DeviceGrain 为本命令分配的设备内顺序号。</param>
    /// <returns>标记后的命令快照。</returns>
    Task<ReliableCommandSnapshot> MarkEnqueuedAsync(long deviceSequence);

    /// <summary>
    /// 开始或恢复一次发送；未到执行时间、等待回复、已经终止或到期时返回空。
    /// </summary>
    /// <returns>允许发送时返回包含 AttemptNo 和 ActionVersion 的报文，否则返回空。</returns>
    Task<ReliableCommandAttempt?> BeginAttemptAsync();

    /// <summary>
    /// 处理调度到期事件，包括回复超时、重试次数耗尽和命令整体过期。
    /// </summary>
    /// <param name="actionVersion">调度时观察到的动作版本，旧版本不会改变状态。</param>
    /// <param name="occurredAt">调度事件发生时间。</param>
    /// <returns>处理后的命令快照。</returns>
    Task<ReliableCommandSnapshot> HandleDueAsync(long actionVersion, DateTimeOffset occurredAt);

    /// <summary>
    /// 记录一次 Gateway 发送结果，并根据结果安排确认等待、下一次重试或终态。
    /// </summary>
    /// <param name="result">当前尝试和动作版本对应的确定或不确定发送结果。</param>
    /// <returns>记录后的命令快照；旧版本结果返回当前状态而不覆盖。</returns>
    Task<ReliableCommandSnapshot> RecordAttemptResultAsync(CommandAttemptResult result);

    /// <summary>
    /// 记录设备协议确认；只接受当前 AttemptNo 和 SessionEpoch，迟到回复不改变状态。
    /// </summary>
    /// <param name="confirmation">协议成功或失败结果及连接代次。</param>
    /// <returns>确认、重试或原状态快照。</returns>
    Task<ReliableCommandSnapshot> ConfirmAsync(DeviceCommandConfirmation confirmation);

    /// <summary>
    /// 取消仍未进入终态的可靠命令。
    /// </summary>
    /// <returns>本次成功进入 Cancelled 时返回 true，未初始化或已有终态返回 false。</returns>
    Task<bool> CancelAsync();

    /// <summary>
    /// 查询当前可靠命令状态；发现整体到期时会先持久化 Expired。
    /// </summary>
    /// <returns>当前命令快照；未初始化时只包含 Grain 主键 MsgId。</returns>
    Task<ReliableCommandSnapshot> GetSnapshotAsync();

    /// <summary>
    /// 清空达到热数据保留期限的终态命令 Payload；执行前会重新校验终态和 TerminalAt，活跃命令不会被修改。
    /// </summary>
    /// <param name="terminalThrough">允许压缩的最晚终态时间。</param>
    /// <returns>本次首次完成 Payload 压缩时返回 true；条件不满足或已经压缩时返回 false。</returns>
    Task<bool> CompactPayloadAsync(DateTimeOffset terminalThrough);

    /// <summary>
    /// 物理删除达到总保留期限的终态 CommandGrain 状态；执行前会重新校验终态和 TerminalAt。
    /// </summary>
    /// <param name="terminalThrough">允许删除的最晚终态时间。</param>
    /// <returns>本次物理删除状态时返回 true；未初始化、非终态或尚未到期时返回 false。</returns>
    Task<bool> PurgeAsync(DateTimeOffset terminalThrough);
}
