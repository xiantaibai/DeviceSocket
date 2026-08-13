namespace DeviceSocket.Cluster.Grains.Scheduling;

/// <summary>
/// 已由 CommandGrain 持久化但尚未确认进入设备执行游标的恢复索引。
/// </summary>
public sealed record PendingCommandRecovery
{
    /// <summary>目标设备编号。</summary>
    public required string DeviceId { get; init; }

    /// <summary>全局命令编号。</summary>
    public required string MsgId { get; init; }

    /// <summary>设备范围内的命令顺序号。</summary>
    public required long DeviceSequence { get; init; }

    /// <summary>最早允许发送的时间。</summary>
    public required DateTimeOffset DueAt { get; init; }
}
