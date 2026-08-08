using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Runtime;

internal sealed record ScenarioExecutionResult(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    MetricSnapshot Measurement,
    IReadOnlyList<PhaseResult> Phases,
    bool Cancelled);
