namespace DeviceSocket.LoadTester.Metrics;

/// <summary>
/// 表示某一时刻节点级连接、协议、重连和流量指标的不可变快照。
/// </summary>
public sealed record MetricSnapshot
{
    public DateTimeOffset TimestampUtc { get; init; }
    public long ExpectedOnline { get; init; }
    public long Connecting { get; init; }
    public long TcpConnected { get; init; }
    public long ProtocolOnline { get; init; }
    public long BackoffWaiting { get; init; }
    public long ConnectionAttempts { get; init; }
    public long ConnectionSucceeded { get; init; }
    public long ConnectionFailed { get; init; }
    public long ConnectionTimeouts { get; init; }
    public long LoginAttempts { get; init; }
    public long LoginSucceeded { get; init; }
    public long LoginFailed { get; init; }
    public long LoginTimeouts { get; init; }
    public long HeartbeatsSent { get; init; }
    public long HeartbeatsConfirmed { get; init; }
    public long HeartbeatTimeouts { get; init; }
    public long ReportsSent { get; init; }
    public long FramesReceived { get; init; }
    public long InvalidFrames { get; init; }
    public long DownstreamFrames { get; init; }
    public long RemoteClosed { get; init; }
    public long WriteTimeouts { get; init; }
    public long ReconnectsScheduled { get; init; }
    public long PermanentlyStopped { get; init; }
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }
    public long ForcedDisconnects { get; init; }
    public long InjectedOutboundDrops { get; init; }
    public long InjectedInboundDrops { get; init; }
    public long InjectedCorruptFrames { get; init; }
    public long InjectedDuplicateFrames { get; init; }
    public long InjectedFragmentedWrites { get; init; }
    public long DownstreamResponsesSent { get; init; }
    public long ApiRequestsStarted { get; init; }
    public long ApiRequestsSucceeded { get; init; }
    public long ApiRequestsFailed { get; init; }
    public long ApiRequestTimeouts { get; init; }
    /// <summary>获取 HTTP 指令阶段累计计划覆盖的目标设备数；fixedOrdinal 来源保持为零。</summary>
    public long ApiTargetsRequired { get; init; }
    /// <summary>获取 HTTP 指令阶段开始时累计在线目标设备数；fixedOrdinal 来源保持为零。</summary>
    public long ApiTargetsOnline { get; init; }
    /// <summary>获取因目标设备已离线而主动跳过的 HTTP 请求累计数。</summary>
    public long ApiRequestsSkippedOffline { get; init; }
    public long GatherReadingCommandsReceived { get; init; }
    public long GatherReadingCommandsResponded { get; init; }
    public long GatherReadingResponsesSent { get; init; }
    public long GatherReadingResponsesDropped { get; init; }
    public long GatherReadingBusinessFailuresSent { get; init; }
    public long GatherReadingDuplicateResponsesSent { get; init; }
    public long OutageCount { get; init; }
    public bool OutageActive { get; init; }
    public long? LastRecoveryMilliseconds { get; init; }
    public IReadOnlyDictionary<string, long> Errors { get; init; } = new Dictionary<string, long>();
    public HistogramSnapshot ConnectionLatency { get; init; } = new MergeableHistogram().Snapshot();
    public HistogramSnapshot LoginLatency { get; init; } = new MergeableHistogram().Snapshot();
    public HistogramSnapshot HeartbeatLatency { get; init; } = new MergeableHistogram().Snapshot();
    public HistogramSnapshot ConnectionScheduleLag { get; init; } = new MergeableHistogram().Snapshot();
    public HistogramSnapshot ApiRequestLatency { get; init; } = new MergeableHistogram().Snapshot();
    public HistogramSnapshot GatherReadingResponseLatency { get; init; } = new MergeableHistogram().Snapshot();
}
