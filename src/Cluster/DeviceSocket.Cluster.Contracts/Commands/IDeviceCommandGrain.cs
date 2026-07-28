using Orleans;

namespace DeviceSocket.Cluster.Contracts.Commands;

/// <summary>
/// 按 MsgId 激活的可靠消息 Grain，独立管理一条重要消息的持久化、重试、到期和确认。
/// </summary>
public interface IDeviceCommandGrain : IGrainWithStringKey
{
    /// <summary>幂等创建消息，并检查相同 MsgId 的持久化内容是否一致。</summary>
    Task<ReliableCommandCreateResult> CreateAsync(ReliableCommandRegistration registration);

    /// <summary>开始或恢复一次发送；不满足重试时间、已经终止或已经到期时返回空值。</summary>
    Task<ReliableCommandAttempt?> BeginAttemptAsync();

    /// <summary>记录一次发送结果，并根据结果安排确认等待或下一次重试。</summary>
    Task<ReliableCommandSnapshot> RecordAttemptResultAsync(CommandAttemptResult result);

    /// <summary>记录设备协议确认并结束自动重试。</summary>
    Task<ReliableCommandSnapshot> ConfirmAsync(DeviceCommandConfirmation confirmation);

    /// <summary>取消仍未进入终态的重要消息。</summary>
    Task<bool> CancelAsync();

    /// <summary>查询当前可靠消息状态。</summary>
    Task<ReliableCommandSnapshot> GetSnapshotAsync();
}
