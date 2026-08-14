using Orleans;

namespace DeviceSocket.Cluster.Contracts.Commands;

/// <summary>
/// 可靠消息的一次正式 Gateway 发送尝试。
/// </summary>
[GenerateSerializer]
public sealed class ReliableCommandAttempt
{
    /// <summary>全局消息编号。</summary>
    [Id(0)] public string MsgId { get; set; } = string.Empty;

    /// <summary>正式发送次数。</summary>
    [Id(1)] public int AttemptNo { get; set; }

    /// <summary>目标设备编号。</summary>
    [Id(2)] public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备协议名称。</summary>
    [Id(3)] public string Protocol { get; set; } = string.Empty;

    /// <summary>完整原始协议报文。</summary>
    [Id(4)] public byte[] Payload { get; set; } = [];

    /// <summary>跨服务链路编号。</summary>
    [Id(5)] public string TraceId { get; set; } = string.Empty;

    /// <summary>本次命令动作版本，旧版本的发送结果不得覆盖新状态。</summary>
    [Id(6)] public long ActionVersion { get; set; }

    /// <summary>是否必须收到设备协议确认；尽力消息为 false，可靠消息为 true。</summary>
    [Id(7)] public bool RequireDeviceConfirmation { get; set; }

    /// <summary>完整报文写入 Socket 后等待设备回复的最长时间。</summary>
    [Id(8)] public TimeSpan ResponseTimeout { get; set; }

    /// <summary>逻辑命令停止发送和接受回复的绝对到期时间。</summary>
    [Id(9)] public DateTimeOffset ExpiresAt { get; set; }
}
