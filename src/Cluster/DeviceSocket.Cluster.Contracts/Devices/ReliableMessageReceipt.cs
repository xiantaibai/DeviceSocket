using DeviceSocket.Cluster.Contracts.Commands;
using Orleans;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// 重要消息的受理和当前投递状态。
/// </summary>
[GenerateSerializer]
public sealed class ReliableMessageReceipt
{
    /// <summary>消息是否被可靠消息子系统接受。</summary>
    [Id(0)] public bool Accepted { get; set; }

    /// <summary>便于调用方稳定判断的结果代码。</summary>
    [Id(1)] public string Code { get; set; } = string.Empty;

    /// <summary>全局消息编号。</summary>
    [Id(2)] public string MsgId { get; set; } = string.Empty;

    /// <summary>当前可靠消息状态。</summary>
    [Id(3)] public ReliableMessageStatus Status { get; set; }

    /// <summary>结果说明。</summary>
    [Id(4)] public string Message { get; set; } = string.Empty;

    /// <summary>当前正式发送次数。</summary>
    [Id(5)] public int AttemptNo { get; set; }

    /// <summary>消息到期时间。</summary>
    [Id(6)] public DateTimeOffset ExpiresAt { get; set; }
}
