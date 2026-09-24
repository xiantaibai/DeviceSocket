namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示跨负载机共享的运行编号、设备分片和计划启动时间。
/// </summary>
public sealed class DistributedOptions
{
    /// <summary>
    /// 获取或设置 <c>distributed.runId</c>；默认空表示运行时生成唯一编号，多台负载机必须配置相同值才能合并为同一轮报告。
    /// </summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置 <c>distributed.shardIndex</c>；默认 0，表示当前负载节点的零基分片序号，取值必须小于 <see cref="ShardCount"/>。
    /// </summary>
    public int ShardIndex { get; set; }

    /// <summary>
    /// 获取或设置 <c>distributed.shardCount</c>；默认 1，表示本轮共同分担设备空间的负载节点总数，零值无效。
    /// </summary>
    public int ShardCount { get; set; } = 1;

    /// <summary>
    /// 获取或设置 <c>distributed.startAtUtc</c>；默认空表示立即启动，设置后各节点按同一 UTC 时刻开始，用于减少多机启动时间偏差。
    /// </summary>
    public DateTimeOffset? StartAtUtc { get; set; }

    /// <summary>
    /// 获取或设置 <c>distributed.randomSeed</c>；默认 null 表示继续从 RunId 派生稳定种子，显式整数使不同 RunId 的改造前后测试仍可复现相同设备随机行为；0 是有效种子。
    /// </summary>
    public int? RandomSeed { get; set; }
}
