namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示一次 Gateway 状态 API 采样结果；请求失败时保留错误而不中断设备负载。
/// </summary>
public sealed record GatewayStatusSnapshot
{
    public DateTimeOffset TimestampUtc { get; init; }
    public bool Available { get; init; }
    public long TcpConnections { get; init; }
    public long AuthenticatedDevices { get; init; }
    public long Protocol3761Connections { get; init; }
    public long Protocol645Connections { get; init; }
    public long ReceivedMessages { get; init; }
    public long SentMessages { get; init; }
    public long SendFailures { get; init; }
    public long ProtocolErrors { get; init; }
    public long PeakTcpConnections { get; init; }
    public long PeakPendingSendMessages { get; init; }
    public long PeakPendingSendBytes { get; init; }
    public long HeartbeatsReceived { get; init; }
    public long HeartbeatConfirmationsSent { get; init; }
    public long HeartbeatLogSuppressed { get; init; }
    public long ApplicationLogDropped { get; init; }
    public long UpstreamLogDropped { get; init; }
    public long DownstreamLogDropped { get; init; }
    public long MaximumSessionScanDurationMilliseconds { get; init; }
    public double ProcessCpuPercent { get; init; }
    public long ProcessWorkingSetBytes { get; init; }
    public long ManagedHeapBytes { get; init; }
    public long ThreadPoolPendingWorkItems { get; init; }
    public string? Error { get; init; }
}
