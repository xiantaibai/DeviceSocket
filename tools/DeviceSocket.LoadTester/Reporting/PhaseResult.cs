using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示一个场景阶段的配置、起止时间及阶段结束指标。
/// </summary>
public sealed record PhaseResult(
    string Name,
    ScenarioPhaseKind Kind,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    int ExpectedOnline,
    MetricSnapshot Metrics);
