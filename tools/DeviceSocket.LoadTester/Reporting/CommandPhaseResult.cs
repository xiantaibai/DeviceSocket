using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示单个 HTTP 指令阶段在当前分片产生的请求和 Gather 读表业务响应增量。
/// </summary>
public sealed record CommandPhaseResult
{
    /// <summary>获取阶段名称，用于关联场景配置和控制台日志。</summary>
    public required string Name { get; init; }
    /// <summary>获取阶段开始等待在线门槛的 UTC 时间；未设置门槛时就是请求执行开始时间。</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }
    /// <summary>获取包含阶段后排空等待的结束 UTC 时间。</summary>
    public required DateTimeOffset EndedAtUtc { get; init; }
    /// <summary>获取当前分片参与阶段的设备数量。</summary>
    public required int LocalDeviceCount { get; init; }
    /// <summary>获取当前分片计划发送的 HTTP 请求数量。</summary>
    public required int PlannedRequests { get; init; }
    /// <summary>获取阶段是否要求老 API 同步等待设备结果。</summary>
    public required bool ShowResult { get; init; }
    /// <summary>获取阶段最终状态；跳过阶段不会产生 HTTP 请求，但仍会进入节点报告。</summary>
    public CommandPhaseStatus Status { get; init; } = CommandPhaseStatus.Completed;
    /// <summary>获取阶段跳过或失败的中文原因；成功完成时为空。</summary>
    public string? StatusReason { get; init; }
    /// <summary>获取当前分片执行阶段前要求达到的本地在线设备数；apiOnly 或显式关闭在线门槛时为零。</summary>
    public int RequiredOnlineCount { get; init; }
    /// <summary>获取等待在线门槛期间观测到的当前分片最大在线设备数，用于解释容量不足导致的阶段跳过。</summary>
    public int MaximumObservedOnlineCount { get; init; }
    /// <summary>获取等待在线门槛实际消耗的毫秒数；未等待时为零。</summary>
    public long OnlineGateWaitMilliseconds { get; init; }
    /// <summary>获取在线门槛是否满足；apiOnly 或显式关闭在线门槛时为空，带风险继续执行时可能为 false。</summary>
    public bool? OnlineGateSatisfied { get; init; }
    /// <summary>获取阶段内实际发起的 API 请求增量。</summary>
    public required long ApiRequestsStarted { get; init; }
    /// <summary>获取阶段内 API 业务成功增量。</summary>
    public required long ApiRequestsSucceeded { get; init; }
    /// <summary>获取阶段内 API 失败增量，包含超时。</summary>
    public required long ApiRequestsFailed { get; init; }
    /// <summary>获取阶段内 API 超时增量。</summary>
    public required long ApiRequestTimeouts { get; init; }
    /// <summary>获取返回 405/407 等明确未进入命令队列的数据预校验拒绝数。</summary>
    public long ApiPrevalidationRejected { get; init; }
    /// <summary>获取 API 估算已进入服务端命令链路的请求数；按发起数减明确预校验拒绝计算，是上界估算而非服务端确认值。</summary>
    public long ApiEstimatedEnqueued { get; init; }
    /// <summary>获取异步阶段被 API 接受的请求数；同步阶段保持为零。</summary>
    public long ApiBusinessAccepted { get; init; }
    /// <summary>获取同步阶段成功返回设备业务结果的请求数；异步阶段保持为零。</summary>
    public long SyncResultsSucceeded { get; init; }
    /// <summary>获取当前阶段独立记录的 HTTP 延迟直方图，避免使用整轮累计分位数误判单个阶段。</summary>
    public HistogramSnapshot ApiRequestLatency { get; init; } = new MergeableHistogram().Snapshot();
    /// <summary>获取当前阶段按稳定错误码聚合的失败次数。</summary>
    public IReadOnlyDictionary<string, long> Errors { get; init; } = new Dictionary<string, long>();
    /// <summary>获取 onlineRegistry 阶段在本节点计划覆盖的目标设备数；fixedOrdinal 来源保持为零。</summary>
    public int ApiTargetsRequired { get; init; }
    /// <summary>获取 onlineRegistry 阶段开始时从在线快照中实际选出的目标设备数；fixedOrdinal 来源保持为零。</summary>
    public int ApiTargetsOnline { get; init; }
    /// <summary>获取因在线目标未补足或发送前设备已离线而主动跳过的 HTTP 请求增量。</summary>
    public long ApiRequestsSkippedOffline { get; init; }
    /// <summary>获取阶段及排空窗口内收到的 Gather 读表命令增量。</summary>
    public required long GatherReadingCommandsReceived { get; init; }
    /// <summary>获取阶段及排空窗口内完成业务应答的 Gather 读表命令增量。</summary>
    public required long GatherReadingCommandsResponded { get; init; }
    /// <summary>获取通过逐请求关联观测到至少一帧原始下行的唯一命令数；重复或重试帧不重复计数。</summary>
    public long RawDownstreamCommandsObserved { get; init; }
    /// <summary>获取原始下行中被当前协议适配器识别为受支持读表格式的唯一命令数。</summary>
    public long ProtocolRecognizedCommands { get; init; }
    /// <summary>获取已关联原始下行中格式合法但当前模拟器尚不支持应答的唯一命令数。</summary>
    public long UnsupportedCommands { get; init; }
    /// <summary>获取按设备和完整报文指纹识别出的重复或服务端重试下行帧数。</summary>
    public long DuplicateDownstreamFrames { get; init; }
    /// <summary>获取估算已入队但在阶段排空窗口内未被模拟设备观测到的命令数。</summary>
    public long GatherReadingCommandsUnobserved { get; init; }
    /// <summary>获取原始下行唯一命令数除以 API 估算已入队数的交付率；估算已入队数为零时返回零。</summary>
    public double RawDeliveryRate { get; init; }
    /// <summary>获取协议已识别唯一命令数除以原始下行唯一命令数的识别率；没有原始下行时返回零。</summary>
    public double ProtocolRecognitionRate { get; init; }
    /// <summary>获取协议已识别命令数除以 API 估算已入队数的兼容交付率；保留旧字段名供既有报告读取。</summary>
    public double GatherReadingDeliveryRate { get; init; }
    /// <summary>获取已关联应答命令数除以协议已识别命令数的阶段应答率；没有已识别命令时返回零。</summary>
    public double GatherReadingResponseRate { get; init; }
    /// <summary>获取当前阶段是否启用了业务排空门槛。</summary>
    public bool DrainEnabled { get; init; }
    /// <summary>获取排空是否在超时前同时达到交付率、应答率和静默时长；未启用排空时为空。</summary>
    public bool? DrainSatisfied { get; init; }
    /// <summary>获取请求生产结束后等待业务排空的实际耗时，单位毫秒。</summary>
    public long DrainWaitMilliseconds { get; init; }
    /// <summary>获取阶段是否触发连续失败、超时率或最大时长保护。</summary>
    public bool CircuitBreakerTripped { get; init; }
    /// <summary>获取熔断触发的中文原因；未触发时为空。</summary>
    public string? CircuitBreakerReason { get; init; }
    /// <summary>获取阶段内观测到的最大连续失败次数。</summary>
    public int MaximumObservedConsecutiveFailures { get; init; }
    /// <summary>获取阶段级成功率、P95、排空交付率和应答率规则是否全部通过；未配置规则也视为通过。</summary>
    public bool AcceptancePassed { get; init; } = true;
}
