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
    GatewayStatusSnapshot? Last)
{
    /// <summary>测试期间观察到的 Gateway 进程最大 CPU 使用率。</summary>
    public double MaximumProcessCpuPercent { get; init; }

    /// <summary>测试期间观察到的 Gateway 最大工作集字节数。</summary>
    public long MaximumProcessWorkingSetBytes { get; init; }

    /// <summary>测试期间观察到的 Gateway 最大托管堆字节数。</summary>
    public long MaximumManagedHeapBytes { get; init; }

    /// <summary>测试期间观察到的 Gateway 最大线程池排队工作项数。</summary>
    public long MaximumThreadPoolPendingWorkItems { get; init; }

    /// <summary>Gateway 进程报告的发送积压消息历史峰值。</summary>
    public long MaximumPeakPendingSendMessages { get; init; }

    /// <summary>Gateway 进程报告的发送积压字节历史峰值。</summary>
    public long MaximumPeakPendingSendBytes { get; init; }

    /// <summary>测试期间观察到的 Session 扫描最大耗时毫秒数。</summary>
    public long MaximumSessionScanDurationMilliseconds { get; init; }

    /// <summary>相对测试前基线新增的程序日志丢弃数量。</summary>
    public long AdditionalApplicationLogDropped { get; init; }

    /// <summary>相对测试前基线新增的设备上行日志丢弃数量。</summary>
    public long AdditionalUpstreamLogDropped { get; init; }

    /// <summary>相对测试前基线新增的设备下发日志丢弃数量。</summary>
    public long AdditionalDownstreamLogDropped { get; init; }

    /// <summary>测试期间在生成 RawHex 前被过滤的心跳数量。</summary>
    public long AdditionalHeartbeatLogSuppressed { get; init; }
}
