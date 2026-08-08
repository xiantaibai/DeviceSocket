using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示节点报告中的一个周期指标和资源采样点。
/// </summary>
public sealed record TimeSeriesEntry(
    MetricSnapshot Metrics,
    ResourceSnapshot Resources,
    GatewayStatusSnapshot? Gateway);
