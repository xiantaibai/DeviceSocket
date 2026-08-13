using Orleans;

namespace DeviceSocket.Cluster.Contracts.Commands;

/// <summary>
/// 创建一条可靠消息时需要持久化的不可变内容。
/// </summary>
[GenerateSerializer]
public sealed class ReliableCommandRegistration
{
    /// <summary>目标设备编号。</summary>
    [Id(0)] public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备协议名称。</summary>
    [Id(1)] public string Protocol { get; set; } = string.Empty;

    /// <summary>完整原始协议报文。</summary>
    [Id(2)] public byte[] Payload { get; set; } = [];

    /// <summary>报文 SHA-256，用于相同 MsgId 的内容一致性校验。</summary>
    [Id(3)] public string PayloadHash { get; set; } = string.Empty;

    /// <summary>跨服务链路编号。</summary>
    [Id(4)] public string TraceId { get; set; } = string.Empty;

    /// <summary>服务端受理时间。</summary>
    [Id(5)] public DateTimeOffset CreatedAt { get; set; }

    /// <summary>停止重试的到期时间。</summary>
    [Id(6)] public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>是否必须收到设备协议确认才算完成。</summary>
    [Id(7)] public bool RequireDeviceConfirmation { get; set; }

    /// <summary>最早允许发送的时间。</summary>
    [Id(8)] public DateTimeOffset NotBefore { get; set; }

    /// <summary>写入 Socket 后等待设备回复的超时时间。</summary>
    [Id(9)] public TimeSpan ResponseTimeout { get; set; }

    /// <summary>同一设备待发送命令的优先级，数值越大越优先。</summary>
    [Id(10)] public int Priority { get; set; }

    /// <summary>设备范围内单调递增的命令序号，用于相同优先级下保持先进先出。</summary>
    [Id(11)] public long DeviceSequence { get; set; }

    /// <summary>包括首次发送在内的最大正式尝试次数。</summary>
    [Id(12)] public int MaxAttempts { get; set; }

    /// <summary>设备所属的命令调度分片。</summary>
    [Id(13)] public int SchedulerShard { get; set; }

    /// <summary>供协议层识别设备应答的稳定匹配键。</summary>
    [Id(14)] public string ResponseMatcher { get; set; } = string.Empty;
}
