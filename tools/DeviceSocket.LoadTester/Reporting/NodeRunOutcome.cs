namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示多节点汇总报告中一个负载分片的运行结论，保留节点身份和失败原因以便快速定位不完整测试。
/// </summary>
public sealed record NodeRunOutcome
{
    /// <summary>
    /// 获取当前节点的零基分片序号。
    /// </summary>
    public required int ShardIndex { get; init; }

    /// <summary>
    /// 获取执行该分片的计算机名称。
    /// </summary>
    public required string NodeName { get; init; }

    /// <summary>
    /// 获取节点最终运行状态；跳过 HTTP 阶段、取消或异常都不会被当作通过。
    /// </summary>
    public required LoadTestRunStatus Status { get; init; }

    /// <summary>
    /// 获取节点异常、取消或阶段跳过的原因；完全通过时为空。
    /// </summary>
    public string? Reason { get; init; }
}
