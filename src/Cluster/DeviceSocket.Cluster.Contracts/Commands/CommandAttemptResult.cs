using Orleans;

namespace DeviceSocket.Cluster.Contracts.Commands;

/// <summary>
/// 一次可靠消息发送尝试的结果。
/// </summary>
[GenerateSerializer]
public sealed class CommandAttemptResult
{
    /// <summary>全局消息编号。</summary>
    [Id(0)] public string MsgId { get; set; } = string.Empty;

    /// <summary>本次正式发送次数。</summary>
    [Id(1)] public int AttemptNo { get; set; }

    /// <summary>发送结果分类。</summary>
    [Id(2)] public CommandAttemptOutcome Outcome { get; set; }

    /// <summary>结果说明。</summary>
    [Id(3)] public string Message { get; set; } = string.Empty;

    /// <summary>实际处理请求的网关节点编号。</summary>
    [Id(4)] public string? GatewayNodeId { get; set; }

    /// <summary>明确写入 Socket 的字节数。</summary>
    [Id(5)] public int BytesWritten { get; set; }

    /// <summary>结果发生时间。</summary>
    [Id(6)] public DateTimeOffset OccurredAt { get; set; }

    /// <summary>供调用方稳定判断失败类型的结果代码。</summary>
    [Id(7)] public string Code { get; set; } = string.Empty;
}
