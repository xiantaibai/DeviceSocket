namespace DeviceSocket.Cluster.Contracts.Commands;

/// <summary>
/// 一次正式发送尝试的结果分类。
/// </summary>
public enum CommandAttemptOutcome
{
    /// <summary>Gateway 明确已经写入设备 Socket。</summary>
    WrittenToSocket = 0,

    /// <summary>明确未写入，可以增加 AttemptNo 后再次发送。</summary>
    RetryableFailure = 1,

    /// <summary>无法判断是否写入，必须保持相同 AttemptNo 重试。</summary>
    DeliveryUncertain = 2,

    /// <summary>参数或协议错误，不应自动重试。</summary>
    PermanentFailure = 3
}
