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
}
