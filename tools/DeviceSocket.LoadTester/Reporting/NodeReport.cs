using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示单个负载分片完整、可追溯且可参与合并的最终报告。
/// </summary>
public sealed record NodeReport
{
    public int SchemaVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required string NodeName { get; init; }
    public required int ShardIndex { get; init; }
    public required int ShardCount { get; init; }
    public required int LocalDeviceCount { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public required LoadTestOptions Options { get; init; }
    public required MetricSnapshot Metrics { get; init; }
    public required ResourceSnapshot Resources { get; init; }
    public GatewayObservationSummary? GatewayObservation { get; init; }
    public required IReadOnlyList<PhaseResult> Phases { get; init; }
    public required AcceptanceResult Acceptance { get; init; }
    public required IReadOnlyList<string> PreflightWarnings { get; init; }
}
