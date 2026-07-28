using Orleans;

namespace DeviceSocket.Cluster.Contracts.Commands;

/// <summary>
/// 可靠消息幂等创建结果。
/// </summary>
[GenerateSerializer]
public sealed class ReliableCommandCreateResult
{
    /// <summary>本次调用是否首次创建消息。</summary>
    [Id(0)] public bool Created { get; set; }

    /// <summary>相同 MsgId 的持久化内容是否与本次请求一致。</summary>
    [Id(1)] public bool ContentMatches { get; set; }

    /// <summary>消息当前状态。</summary>
    [Id(2)] public ReliableCommandSnapshot Snapshot { get; set; } = new();
}
