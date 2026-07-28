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
}
