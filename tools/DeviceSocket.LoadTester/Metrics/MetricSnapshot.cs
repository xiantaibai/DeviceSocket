namespace DeviceSocket.LoadTester.Metrics;

/// <summary>
/// 表示某一时刻节点级连接、协议、重连和流量指标的不可变快照。
/// </summary>
public sealed record MetricSnapshot
{
    /// <summary>获取快照采集的 UTC 时间。</summary>
    public DateTimeOffset TimestampUtc { get; init; }
    /// <summary>获取当前阶段要求保持的协议在线设备数。</summary>
    public long ExpectedOnline { get; init; }
    /// <summary>获取正在等待 TCP 建连完成的设备数。</summary>
    public long Connecting { get; init; }
    /// <summary>获取已建立物理 TCP 连接的设备数。</summary>
    public long TcpConnected { get; init; }
    /// <summary>获取已完成协议登录的在线设备数。</summary>
    public long ProtocolOnline { get; init; }
    /// <summary>获取正在自动重连退避中的设备数。</summary>
    public long BackoffWaiting { get; init; }
    /// <summary>获取累计 TCP 建连尝试数。</summary>
    public long ConnectionAttempts { get; init; }
    /// <summary>获取累计 TCP 建连成功数。</summary>
    public long ConnectionSucceeded { get; init; }
    /// <summary>获取累计 TCP 建连失败数。</summary>
    public long ConnectionFailed { get; init; }
    /// <summary>获取累计 TCP 建连超时数。</summary>
    public long ConnectionTimeouts { get; init; }
    /// <summary>获取累计协议登录尝试数。</summary>
    public long LoginAttempts { get; init; }
    /// <summary>获取累计协议登录成功数。</summary>
    public long LoginSucceeded { get; init; }
    /// <summary>获取累计协议登录失败数。</summary>
    public long LoginFailed { get; init; }
    /// <summary>获取累计协议登录确认超时数。</summary>
    public long LoginTimeouts { get; init; }
    /// <summary>获取累计心跳发送数。</summary>
    public long HeartbeatsSent { get; init; }
    /// <summary>获取累计心跳确认数。</summary>
    public long HeartbeatsConfirmed { get; init; }
    /// <summary>获取累计心跳确认超时数。</summary>
    public long HeartbeatTimeouts { get; init; }
    /// <summary>获取累计主动业务上报发送数。</summary>
    public long ReportsSent { get; init; }
    /// <summary>获取累计接收协议帧数，包含非法帧。</summary>
    public long FramesReceived { get; init; }
    /// <summary>获取累计非法协议帧数。</summary>
    public long InvalidFrames { get; init; }
    /// <summary>获取累计服务端下发业务帧数。</summary>
    public long DownstreamFrames { get; init; }
    /// <summary>获取累计远端主动关闭连接数。</summary>
    public long RemoteClosed { get; init; }
    /// <summary>获取累计 Socket 写入超时数。</summary>
    public long WriteTimeouts { get; init; }
    /// <summary>获取累计自动重连调度数。</summary>
    public long ReconnectsScheduled { get; init; }
    /// <summary>获取达到最大重连次数后永久停止的设备数。</summary>
    public long PermanentlyStopped { get; init; }
    /// <summary>获取累计发送字节数。</summary>
    public long BytesSent { get; init; }
    /// <summary>获取累计接收字节数。</summary>
    public long BytesReceived { get; init; }
    /// <summary>获取故障阶段由工具主动断开的设备次数。</summary>
    public long ForcedDisconnects { get; init; }
    /// <summary>获取累计注入的上行丢帧数。</summary>
    public long InjectedOutboundDrops { get; init; }
    /// <summary>获取累计注入的下行丢帧数。</summary>
    public long InjectedInboundDrops { get; init; }
    /// <summary>获取累计注入的错误校验帧数。</summary>
    public long InjectedCorruptFrames { get; init; }
    /// <summary>获取累计注入的重复帧数。</summary>
    public long InjectedDuplicateFrames { get; init; }
    /// <summary>获取累计执行的半包写入数。</summary>
    public long InjectedFragmentedWrites { get; init; }
    /// <summary>获取累计发送的下行命令应答帧数。</summary>
    public long DownstreamResponsesSent { get; init; }
    /// <summary>获取累计发起的 HTTP API 请求数。</summary>
    public long ApiRequestsStarted { get; init; }
    /// <summary>获取累计 HTTP 状态和业务结果均成功的请求数。</summary>
    public long ApiRequestsSucceeded { get; init; }
    /// <summary>获取累计 HTTP 或业务失败请求数，包含超时。</summary>
    public long ApiRequestsFailed { get; init; }
    /// <summary>获取累计 HTTP 请求超时数。</summary>
    public long ApiRequestTimeouts { get; init; }
    /// <summary>获取 HTTP 指令阶段累计计划覆盖的目标设备数；fixedOrdinal 来源保持为零。</summary>
    public long ApiTargetsRequired { get; init; }
    /// <summary>获取 HTTP 指令阶段开始时累计在线目标设备数；fixedOrdinal 来源保持为零。</summary>
    public long ApiTargetsOnline { get; init; }
    /// <summary>获取因目标设备已离线而主动跳过的 HTTP 请求累计数。</summary>
    public long ApiRequestsSkippedOffline { get; init; }
    /// <summary>获取模拟设备累计收到的 Gather 读表命令数。</summary>
    public long GatherReadingCommandsReceived { get; init; }
    /// <summary>获取模拟设备累计完成业务应答的 Gather 读表命令数。</summary>
    public long GatherReadingCommandsResponded { get; init; }
    /// <summary>获取累计发送的 Gather 读表响应帧数，包含重复帧。</summary>
    public long GatherReadingResponsesSent { get; init; }
    /// <summary>获取按场景规则主动丢弃的 Gather 读表响应数。</summary>
    public long GatherReadingResponsesDropped { get; init; }
    /// <summary>获取累计发送的 Gather 读表业务失败响应数。</summary>
    public long GatherReadingBusinessFailuresSent { get; init; }
    /// <summary>获取累计额外发送的 Gather 重复响应帧数。</summary>
    public long GatherReadingDuplicateResponsesSent { get; init; }
    /// <summary>获取达到过完整在线后累计发生的连续掉线事件数。</summary>
    public long OutageCount { get; init; }
    /// <summary>获取快照时是否存在尚未恢复到目标在线数的事件。</summary>
    public bool OutageActive { get; init; }
    /// <summary>获取最近一次已恢复事件耗时，单位毫秒；尚无恢复事件时为空。</summary>
    public long? LastRecoveryMilliseconds { get; init; }
    /// <summary>获取所有已恢复或仍进行中的掉线事件；列表按开始时间排序。</summary>
    public IReadOnlyList<OutageEventSnapshot> Outages { get; init; } = [];
    /// <summary>获取本轮所有已恢复事件中的最长恢复耗时；尚无已恢复事件时为空。</summary>
    public long? WorstRecoveryMilliseconds { get; init; }
    /// <summary>获取当前已连续保持完整目标在线的时长，单位毫秒；当前未满目标时为零。</summary>
    public long CurrentStableOnlineMilliseconds { get; init; }
    /// <summary>获取本轮曾连续保持完整目标在线的最长时长，单位毫秒。</summary>
    public long MaximumStableOnlineMilliseconds { get; init; }
    /// <summary>获取当前阶段名称；未进入显式阶段时为空字符串。</summary>
    public string CurrentPhaseName { get; init; } = string.Empty;
    /// <summary>获取按稳定错误码累计的错误次数字典。</summary>
    public IReadOnlyDictionary<string, long> Errors { get; init; } = new Dictionary<string, long>();
    /// <summary>获取 TCP 建连耗时的可合并直方图。</summary>
    public HistogramSnapshot ConnectionLatency { get; init; } = new MergeableHistogram().Snapshot();
    /// <summary>获取协议登录确认耗时的可合并直方图。</summary>
    public HistogramSnapshot LoginLatency { get; init; } = new MergeableHistogram().Snapshot();
    /// <summary>获取心跳确认耗时的可合并直方图。</summary>
    public HistogramSnapshot HeartbeatLatency { get; init; } = new MergeableHistogram().Snapshot();
    /// <summary>获取连接计划时刻到实际开始时刻滞后的可合并直方图。</summary>
    public HistogramSnapshot ConnectionScheduleLag { get; init; } = new MergeableHistogram().Snapshot();
    /// <summary>获取 HTTP API 端到端耗时的可合并直方图。</summary>
    public HistogramSnapshot ApiRequestLatency { get; init; } = new MergeableHistogram().Snapshot();
    /// <summary>获取模拟设备收到读表命令到发送响应耗时的可合并直方图。</summary>
    public HistogramSnapshot GatherReadingResponseLatency { get; init; } = new MergeableHistogram().Snapshot();
}
