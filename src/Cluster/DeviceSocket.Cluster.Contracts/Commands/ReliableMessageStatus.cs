namespace DeviceSocket.Cluster.Contracts.Commands;

/// <summary>
/// 一条重要设备消息的持久化生命周期状态。
/// </summary>
public enum ReliableMessageStatus
{
    /// <summary>已持久化，等待首次发送。</summary>
    Pending = 0,

    /// <summary>正在执行一次 Gateway 下发。</summary>
    Dispatching = 1,

    /// <summary>已经写入 Socket，等待设备协议确认。</summary>
    AwaitingConfirmation = 2,

    /// <summary>设备已经返回成功确认。</summary>
    Confirmed = 3,

    /// <summary>消息不要求设备确认，并已在成功写入 Socket 后完成。</summary>
    CompletedWithoutConfirmation = 4,

    /// <summary>确定未写入，等待下一次正式重试。</summary>
    RetryPending = 5,

    /// <summary>超过消息到期时间。</summary>
    Expired = 6,

    /// <summary>出现不可重试错误。</summary>
    Failed = 7,

    /// <summary>调用方已经取消消息。</summary>
    Cancelled = 8,

    /// <summary>命令已持久化，但设备执行游标尚未确认接管，可由分片调度器修复。</summary>
    EnqueuePending = 9,

    /// <summary>命令已经进入设备等待队列，尚未取得设备级执行权。</summary>
    Queued = 10
}
