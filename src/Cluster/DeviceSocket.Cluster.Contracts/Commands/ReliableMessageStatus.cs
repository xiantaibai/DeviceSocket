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

    /// <summary>不要求设备确认的消息已经完成。</summary>
    Completed = 4,

    /// <summary>确定未写入，等待下一次正式重试。</summary>
    RetryPending = 5,

    /// <summary>超过消息到期时间。</summary>
    Expired = 6,

    /// <summary>出现不可重试错误。</summary>
    Failed = 7,

    /// <summary>调用方已经取消消息。</summary>
    Cancelled = 8
}
