namespace DeviceSocket.Cluster.Grains.Scheduling;

/// <summary>
/// 从持久化命令索引读取的设备队首候选项，不包含报文正文。
/// </summary>
public sealed record DeviceCommandQueueEntry
{
    /// <summary>全局命令编号。</summary>
    public required string MsgId { get; init; }

    /// <summary>最早允许发送的时间。</summary>
    public required DateTimeOffset DueAt { get; init; }

    /// <summary>同一设备中的优先级。</summary>
    public required int Priority { get; init; }

    /// <summary>设备范围内的顺序号。</summary>
    public required long DeviceSequence { get; init; }
}
