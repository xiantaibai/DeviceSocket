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
}
