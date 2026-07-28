using Orleans;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// 不持久化且失败后允许丢弃的普通设备消息。
/// </summary>
[GenerateSerializer]
public sealed class BestEffortDeviceMessage
{
    /// <summary>日志关联编号；为空时由 DeviceGrain 自动生成。</summary>
    [Id(0)] public string MsgId { get; set; } = string.Empty;

    /// <summary>设备协议名称。</summary>
    [Id(1)] public string Protocol { get; set; } = string.Empty;

    /// <summary>已经完成协议编码的完整原始报文。</summary>
    [Id(2)] public byte[] Payload { get; set; } = [];

    /// <summary>跨服务调用链路编号。</summary>
    [Id(3)] public string TraceId { get; set; } = string.Empty;
}
