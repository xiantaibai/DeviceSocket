using Orleans;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// Gateway 完成协议匹配后交给 DeviceGrain 的设备命令回复。
/// </summary>
[GenerateSerializer]
public sealed class DeviceCommandReply
{
    /// <summary>目标设备编号。</summary>
    [Id(0)] public string DeviceId { get; set; } = string.Empty;

    /// <summary>被回复的全局命令编号。</summary>
    [Id(1)] public string MsgId { get; set; } = string.Empty;

    /// <summary>被回复的正式发送次数。</summary>
    [Id(2)] public int AttemptNo { get; set; }

    /// <summary>产生回复的设备连接代次。</summary>
    [Id(3)] public long SessionEpoch { get; set; }

    /// <summary>协议响应是否表示设备执行成功。</summary>
    [Id(4)] public bool Success { get; set; }

    /// <summary>协议层解析得到的结果说明。</summary>
    [Id(5)] public string Message { get; set; } = string.Empty;

    /// <summary>设备返回的完整原始协议报文。</summary>
    [Id(6)] public byte[] Payload { get; set; } = [];

    /// <summary>回复到达 Gateway 的时间。</summary>
    [Id(7)] public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>跨服务链路编号。</summary>
    [Id(8)] public string TraceId { get; set; } = string.Empty;
}
