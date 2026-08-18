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
    /// <summary>获取阶段及排空窗口内收到的 Gather 读表命令增量。</summary>
    public required long GatherReadingCommandsReceived { get; init; }
    /// <summary>获取阶段及排空窗口内完成业务应答的 Gather 读表命令增量。</summary>
    public required long GatherReadingCommandsResponded { get; init; }
}
