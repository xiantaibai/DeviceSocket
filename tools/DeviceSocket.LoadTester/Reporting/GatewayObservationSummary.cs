namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示一轮测试期间 Gateway 可用性和连接数量的峰值观测。
/// </summary>
public sealed record GatewayObservationSummary(
    long Samples,
    long UnavailableSamples,
    long? BaselineTcpConnections,
    long? BaselineAuthenticatedDevices,
    long MaximumTcpConnections,
    long MaximumAuthenticatedDevices,
    long MaximumAdditionalTcpConnections,
    long MaximumAdditionalAuthenticatedDevices,
    GatewayStatusSnapshot? Last);
