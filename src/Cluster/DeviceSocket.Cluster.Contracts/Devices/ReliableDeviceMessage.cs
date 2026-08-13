using Orleans;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// 需要先持久化并在到期前重试的重要设备消息。
/// </summary>
[GenerateSerializer]
public sealed class ReliableDeviceMessage
{
    /// <summary>全局消息编号；调用方重试时必须保持不变。</summary>
    [Id(0)] public string MsgId { get; set; } = string.Empty;

    /// <summary>设备协议名称。</summary>
    [Id(1)] public string Protocol { get; set; } = string.Empty;

    /// <summary>已经完成协议编码的完整原始报文。</summary>
    [Id(2)] public byte[] Payload { get; set; } = [];

    /// <summary>消息到期时间；为空时由服务端设置为受理时间之后五分钟。</summary>
    [Id(3)] public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>是否必须收到设备协议确认才结束；可靠命令入口固定按 true 执行，字段仅为兼容既有契约保留。</summary>
    [Id(4)] public bool RequireDeviceConfirmation { get; set; } = true;

    /// <summary>跨服务调用链路编号。</summary>
    [Id(5)] public string TraceId { get; set; } = string.Empty;

    /// <summary>最早允许发送的时间；为空表示受理后立即具备执行资格。</summary>
    [Id(6)] public DateTimeOffset? NotBefore { get; set; }

    /// <summary>写入设备 Socket 后等待协议回复的最长时间；为空时使用 Silo 默认值。</summary>
    [Id(7)] public TimeSpan? ResponseTimeout { get; set; }

    /// <summary>同一设备等待队列中的优先级；数值越大越优先，但不会抢占正在执行的命令。</summary>
    [Id(8)] public int Priority { get; set; }

    /// <summary>包括首次发送在内的最大正式尝试次数；零值表示使用 Silo 默认值。</summary>
    [Id(9)] public int MaxAttempts { get; set; }

    /// <summary>供协议层识别应答的稳定匹配键；为空时由协议 Handler 使用 MsgId 匹配。</summary>
    [Id(10)] public string ResponseMatcher { get; set; } = string.Empty;
}
