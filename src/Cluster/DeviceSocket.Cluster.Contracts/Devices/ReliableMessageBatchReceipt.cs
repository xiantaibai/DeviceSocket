using Orleans;

namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// 同一设备批量可靠命令的受理结果。
/// </summary>
[GenerateSerializer]
public sealed class ReliableMessageBatchReceipt
{
    /// <summary>批次中的命令总数。</summary>
    [Id(0)] public int TotalCount { get; set; }

    /// <summary>成功进入可靠命令子系统的数量。</summary>
    [Id(1)] public int AcceptedCount { get; set; }

    /// <summary>每条命令按输入顺序返回的受理结果。</summary>
    [Id(2)] public ReliableMessageReceipt[] Items { get; set; } = [];
}
