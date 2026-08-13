using Orleans;

namespace DeviceSocket.Cluster.Contracts.Commands;

/// <summary>
/// Gateway 完成协议响应匹配后上报的重要消息确认。
/// </summary>
[GenerateSerializer]
public sealed class DeviceCommandConfirmation
{
    /// <summary>全局消息编号。</summary>
    [Id(0)] public string MsgId { get; set; } = string.Empty;

    /// <summary>设备是否返回成功确认。</summary>
    [Id(1)] public bool Success { get; set; }

    /// <summary>设备响应说明。</summary>
    [Id(2)] public string Message { get; set; } = string.Empty;

    /// <summary>确认发生时间。</summary>
    [Id(3)] public DateTimeOffset ConfirmedAt { get; set; }

    /// <summary>产生回复的正式发送次数；与当前尝试不一致时按迟到回复忽略。</summary>
    [Id(4)] public int AttemptNo { get; set; }

    /// <summary>产生回复的设备连接代次，用于拒绝断线重连前的旧回复。</summary>
    [Id(5)] public long SessionEpoch { get; set; }

    /// <summary>设备返回的原始协议报文，供后续业务审计或协议诊断使用。</summary>
    [Id(6)] public byte[] Payload { get; set; } = [];

    /// <summary>跨服务链路编号。</summary>
    [Id(7)] public string TraceId { get; set; } = string.Empty;
}
