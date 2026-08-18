using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示同一 RunId 下所有负载分片合并后的集群级报告。
/// </summary>
public sealed record AggregateReport
{
    /// <summary>
    /// 获取汇总报告结构版本；当前固定为 1，用于报告读取方判断兼容性。
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// 获取所有分片共享的运行编号。
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// 获取最早节点开始执行的 UTC 时刻。
    /// </summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// 获取最晚节点结束执行的 UTC 时刻。
    /// </summary>
    public required DateTimeOffset EndedAtUtc { get; init; }

    /// <summary>
    /// 获取参与汇总且通过完整性校验的分片报告数量。
    /// </summary>
    public required int NodeCount { get; init; }

    /// <summary>
    /// 获取所有分片实际分配的虚拟设备总数。
    /// </summary>
    public required int DeviceCount { get; init; }

    /// <summary>
    /// 获取累计计数求和、瞬时仪表求和且延迟直方图合并后的集群级指标。
    /// </summary>
    public required MetricSnapshot Metrics { get; init; }

    /// <summary>
    /// 获取各负载机对同一 Gateway 观测结果的去重聚合；未启用服务端观测时为空。
    /// </summary>
    public GatewayObservationSummary? GatewayObservation { get; init; }

    /// <summary>
    /// 获取使用汇总指标重新计算的集群级验收结论。
    /// </summary>
    public required AcceptanceResult Acceptance { get; init; }

    /// <summary>
    /// 获取汇总运行状态；任一节点异常、取消或跳过阶段都会传播到此状态。
    /// </summary>
    public required LoadTestRunStatus RunStatus { get; init; }

    /// <summary>
    /// 获取按分片序号排列的节点运行结论和失败原因。
    /// </summary>
    public required IReadOnlyList<NodeRunOutcome> NodeOutcomes { get; init; }

    /// <summary>
    /// 获取按分片序号排列的计算机名称；保留该字段以兼容既有报告读取工具。
    /// </summary>
    public required IReadOnlyList<string> Nodes { get; init; }
}
