using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示同一 RunId 下所有负载分片合并后的集群级报告。
/// </summary>
public sealed record AggregateReport
{
    public int SchemaVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public required int NodeCount { get; init; }
    public required int DeviceCount { get; init; }
    public required MetricSnapshot Metrics { get; init; }
    public GatewayObservationSummary? GatewayObservation { get; init; }
    public required AcceptanceResult Acceptance { get; init; }
    public required IReadOnlyList<string> Nodes { get; init; }
}
