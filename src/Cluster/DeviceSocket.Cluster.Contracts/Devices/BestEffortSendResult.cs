using Orleans;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// 普通消息单次发送结果。
/// </summary>
[GenerateSerializer]
public sealed class BestEffortSendResult
{
    /// <summary>是否已经明确写入设备 Socket。</summary>
    [Id(0)] public bool Success { get; set; }

    /// <summary>本次消息编号。</summary>
    [Id(1)] public string MsgId { get; set; } = string.Empty;

    /// <summary>单次发送状态。</summary>
    [Id(2)] public BestEffortSendStatus Status { get; set; }

    /// <summary>结果或丢弃原因。</summary>
    [Id(3)] public string Message { get; set; } = string.Empty;

    /// <summary>实际处理请求的网关节点编号。</summary>
    [Id(4)] public string? GatewayNodeId { get; set; }

    /// <summary>明确写入 Socket 的字节数。</summary>
    [Id(5)] public int BytesWritten { get; set; }
}
