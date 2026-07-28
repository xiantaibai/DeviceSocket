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

    /// <summary>是否必须收到设备协议确认才结束重试；仅在已经实现对应协议响应匹配时启用。</summary>
    [Id(4)] public bool RequireDeviceConfirmation { get; set; }

    /// <summary>跨服务调用链路编号。</summary>
    [Id(5)] public string TraceId { get; set; } = string.Empty;
}
