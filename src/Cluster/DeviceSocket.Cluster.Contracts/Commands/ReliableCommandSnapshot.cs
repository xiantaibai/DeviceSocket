using Orleans;

namespace DeviceSocket.Cluster.Contracts.Commands;

/// <summary>
/// 一条重要消息可持久化和查询的完整状态快照。
/// </summary>
[GenerateSerializer]
public sealed class ReliableCommandSnapshot
{
    /// <summary>全局消息编号。</summary>
    [Id(0)] public string MsgId { get; set; } = string.Empty;

    /// <summary>目标设备编号。</summary>
    [Id(1)] public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备协议名称。</summary>
    [Id(2)] public string Protocol { get; set; } = string.Empty;

    /// <summary>完整原始协议报文。</summary>
    [Id(3)] public byte[] Payload { get; set; } = [];

    /// <summary>原始报文 SHA-256。</summary>
    [Id(4)] public string PayloadHash { get; set; } = string.Empty;

    /// <summary>跨服务链路编号。</summary>
    [Id(5)] public string TraceId { get; set; } = string.Empty;

    /// <summary>当前生命周期状态。</summary>
    [Id(6)] public ReliableMessageStatus Status { get; set; }

    /// <summary>当前正式发送次数。</summary>
    [Id(7)] public int AttemptNo { get; set; }

    /// <summary>最近一次状态说明或错误原因。</summary>
    [Id(8)] public string Message { get; set; } = string.Empty;

    /// <summary>服务端受理时间。</summary>
    [Id(9)] public DateTimeOffset CreatedAt { get; set; }

    /// <summary>最近一次状态更新时间。</summary>
    [Id(10)] public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>停止重试的到期时间。</summary>
    [Id(11)] public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>下一次允许重试的时间。</summary>
    [Id(12)] public DateTimeOffset? NextRetryAt { get; set; }

    /// <summary>是否必须收到设备协议确认。</summary>
    [Id(13)] public bool RequireDeviceConfirmation { get; set; }

    /// <summary>最早允许发送的时间。</summary>
    [Id(14)] public DateTimeOffset NotBefore { get; set; }

    /// <summary>当前尝试等待设备回复的绝对截止时间。</summary>
    [Id(15)] public DateTimeOffset? ReplyDeadline { get; set; }

    /// <summary>写入 Socket 后等待设备回复的超时时间。</summary>
    [Id(16)] public TimeSpan ResponseTimeout { get; set; }

    /// <summary>包括首次发送在内的最大正式尝试次数。</summary>
    [Id(17)] public int MaxAttempts { get; set; }

    /// <summary>同一设备等待队列中的优先级。</summary>
    [Id(18)] public int Priority { get; set; }

    /// <summary>设备范围内的命令顺序号。</summary>
    [Id(19)] public long DeviceSequence { get; set; }

    /// <summary>命令每次改变下一动作时递增的版本，用于丢弃重复或迟到唤醒。</summary>
    [Id(20)] public long ActionVersion { get; set; }

    /// <summary>设备所属的命令调度分片。</summary>
    [Id(21)] public int SchedulerShard { get; set; }

    /// <summary>供协议层识别设备应答的稳定匹配键。</summary>
    [Id(22)] public string ResponseMatcher { get; set; } = string.Empty;

    /// <summary>设备游标未接管命令时，允许恢复扫描介入的时间。</summary>
    [Id(23)] public DateTimeOffset? EnqueueRecoveryAt { get; set; }

    /// <summary>本次实际下发所绑定的设备连接代次，用于拒绝旧连接迟到回复。</summary>
    [Id(24)] public long ActiveSessionEpoch { get; set; }

    /// <summary>命令首次进入 Confirmed、CompletedWithoutConfirmation、Expired、Failed 或 Cancelled 终态的时间。</summary>
    [Id(25)] public DateTimeOffset? TerminalAt { get; set; }

    /// <summary>终态命令为降低热存储容量而清空 Payload 的时间；空值表示尚未压缩。</summary>
    [Id(26)] public DateTimeOffset? PayloadCompactedAt { get; set; }
}
