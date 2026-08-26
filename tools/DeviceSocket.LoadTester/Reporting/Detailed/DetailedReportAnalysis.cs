using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>
/// 表示由 LoadTester 节点报告、客户端时间序列和可选 TcpCenter 指标归一化得到的离线分析事实；所有展示模板只读取该模型。
/// </summary>
internal sealed record DetailedReportAnalysis
{
    /// <summary>获取分析模型结构版本；版本 5 将 HTTP 原始下行交付、协议识别和设备应答拆分展示。</summary>
    public int SchemaVersion { get; init; } = 5;

    /// <summary>获取当前负载测试运行编号。</summary>
    public required string RunId { get; init; }

    /// <summary>获取报告标题。</summary>
    public required string Title { get; init; }

    /// <summary>获取环境说明；未由命令行提供时为“未填写”。</summary>
    public required string Environment { get; init; }

    /// <summary>获取服务版本说明；未由命令行提供时为“未填写”。</summary>
    public required string ServiceVersion { get; init; }

    /// <summary>获取测试或报告整理人员；未由命令行提供时为“未填写”。</summary>
    public required string Tester { get; init; }

    /// <summary>获取报告生成的 UTC 时间；该值在每次执行 <c>report generate</c> 时自动产生，不从背景 JSON 伪造。</summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>获取由背景 JSON、节点摘要和自动推断合并后的测试背景，用于验收版和排查版共同追溯环境。</summary>
    public required DetailedReportBackground Background { get; init; }

    /// <summary>获取最早节点开始时间。</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>获取最晚节点结束时间。</summary>
    public required DateTimeOffset EndedAtUtc { get; init; }

    /// <summary>获取参与分析的 LoadTester 分片数量。</summary>
    public required int NodeCount { get; init; }

    /// <summary>获取参与分析的虚拟设备数量。</summary>
    public required int DeviceCount { get; init; }

    /// <summary>获取验收和里程碑计算采用的目标设备数。</summary>
    public required int TargetDeviceCount { get; init; }

    /// <summary>获取当前场景是否声明了计划服务端重启；用于区分预期 PID 变化和恢复后的二次掉线。</summary>
    public required bool ExpectedServerRestart { get; init; }

    /// <summary>获取各分片测量快照合并后的客户端累计指标。</summary>
    public required MetricSnapshot ClientMetrics { get; init; }

    /// <summary>获取负载机资源峰值。</summary>
    public required LoadGeneratorResourceSummary LoadGeneratorResources { get; init; }

    /// <summary>获取按时间合并后的客户端连接和错误趋势点。</summary>
    public required IReadOnlyList<ClientTimelinePoint> ClientTimeline { get; init; }

    /// <summary>获取与测试时间窗对齐的 TcpCenter 趋势点；缺少服务端文件时为空。</summary>
    public required IReadOnlyList<TcpCenterTimelinePoint> ServerTimeline { get; init; }

    /// <summary>获取 TcpCenter 峰值、区间增量和里程碑摘要；缺少服务端文件时为空。</summary>
    public TcpCenterAnalysisSummary? Server { get; init; }

    /// <summary>获取 TCP cooldown 阶段对应的服务端摘要；验收与自动问题诊断不会使用该窗口，避免计划缩容造成误报。</summary>
    public TcpCenterAnalysisSummary? CooldownServer { get; init; }

    /// <summary>获取 TCP cooldown 阶段对应的服务端趋势点；没有显式 cooldown 或无重叠指标时为空。</summary>
    public IReadOnlyList<TcpCenterTimelinePoint> CooldownServerTimeline { get; init; } = [];

    /// <summary>获取按名称合并后的 HTTP 指令阶段。</summary>
    public required IReadOnlyList<DetailedCommandPhaseSummary> CommandPhases { get; init; }

    /// <summary>获取可判定、失败或因缺少数据而不可评估的验收项。</summary>
    public required IReadOnlyList<DetailedAcceptanceCheck> AcceptanceChecks { get; init; }

    /// <summary>获取包含证据、影响、建议和置信度的自动诊断问题。</summary>
    public required IReadOnlyList<DiagnosticFinding> Findings { get; init; }

    /// <summary>获取输入缺失、时间不重叠、JSONL 截断等数据质量警告。</summary>
    public required IReadOnlyList<string> DataWarnings { get; init; }

    /// <summary>获取可选改造前基线与当前结果的关键指标对比。</summary>
    public required IReadOnlyList<ReportComparisonItem> Comparisons { get; init; }

    /// <summary>获取综合验收是否通过；任何失败或不可评估项都会使结果为否，避免缺指标被误判为通过。</summary>
    public bool AcceptancePassed => AcceptanceChecks.All(item => item.Status == DetailedAcceptanceStatus.Passed);
}

/// <summary>
/// 表示最终写入报告和 <c>analysis.json</c> 的测试背景；所有空字段已转换为明确的“未填写”。
/// </summary>
internal sealed record DetailedReportBackground
{
    /// <summary>获取背景信息来源路径；未导入 JSON 时为“自动推断”。</summary>
    public required string Source { get; init; }

    /// <summary>获取测试场景名称；优先使用背景 JSON，否则来自节点摘要中的 <c>scenario.name</c>。</summary>
    public required string ScenarioName { get; init; }

    /// <summary>获取测试覆盖范围说明；背景 JSON 未提供时根据节点配置自动生成。</summary>
    public required string ScenarioDescription { get; init; }

    /// <summary>获取被测服务器或资产名称。</summary>
    public required string ServerName { get; init; }

    /// <summary>获取服务器操作系统版本。</summary>
    public required string OperatingSystem { get; init; }

    /// <summary>获取服务器 CPU 规格。</summary>
    public required string Cpu { get; init; }

    /// <summary>获取服务器内存规格。</summary>
    public required string Memory { get; init; }

    /// <summary>获取服务器网络规格和环境说明。</summary>
    public required string Network { get; init; }

    /// <summary>获取被测服务名称。</summary>
    public required string ServiceName { get; init; }

    /// <summary>获取被测服务版本；与报告顶层服务版本保持一致。</summary>
    public required string ServiceVersion { get; init; }

    /// <summary>获取生成被测程序的 Git 分支。</summary>
    public required string CodeBranch { get; init; }

    /// <summary>获取生成被测程序的 Git Commit。</summary>
    public required string CodeCommit { get; init; }

    /// <summary>获取由背景 JSON 导入的关键服务运行参数。</summary>
    public required IReadOnlyList<DetailedReportParameter> ServiceParameters { get; init; }
}

/// <summary>
/// 表示最终报告测试背景中的一项关键服务运行参数。
/// </summary>
internal sealed record DetailedReportParameter
{
    /// <summary>获取参数显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>获取包含单位的参数显示值。</summary>
    public required string Value { get; init; }
}

/// <summary>
/// 表示单个时刻所有 LoadTester 分片的连接、在线和累计错误聚合值。
/// </summary>
internal sealed record ClientTimelinePoint
{
    /// <summary>获取 UTC 采样时间。</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>获取当前目标在线数。</summary>
    public required long ExpectedOnline { get; init; }

    /// <summary>获取物理 TCP 在线数。</summary>
    public required long TcpConnected { get; init; }

    /// <summary>获取已收到协议登录确认的在线数。</summary>
    public required long ProtocolOnline { get; init; }

    /// <summary>获取累计连接失败数。</summary>
    public required long ConnectionFailed { get; init; }

    /// <summary>获取累计本地端口已占用错误数。</summary>
    public required long AddressAlreadyInUse { get; init; }
}

/// <summary>
/// 表示与 LoadTester 测试窗口对齐后的单个 TcpCenter 服务指标点。
/// </summary>
internal sealed record TcpCenterTimelinePoint
{
    /// <summary>获取按指定服务端时区转换后的 UTC 时间。</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>获取 TcpCenter 进程编号。</summary>
    public required int ProcessId { get; init; }

    /// <summary>获取 HP-Socket 物理连接数。</summary>
    public required int SocketConnections { get; init; }

    /// <summary>获取已绑定设备地址的连接数。</summary>
    public required int BoundDeviceConnections { get; init; }

    /// <summary>获取已建立业务线路并可以处理命令的设备数。</summary>
    public required int BusinessOnlineDevices { get; init; }

    /// <summary>获取已绑定地址但尚未建立业务线路的设备数。</summary>
    public required int BoundWithoutBusinessLine { get; init; }

    /// <summary>获取当前待处理登录数。</summary>
    public required int PendingLogin { get; init; }

    /// <summary>获取当前待处理心跳数。</summary>
    public required int PendingHeartbeat { get; init; }

    /// <summary>获取当前待建立业务线路的命令数。</summary>
    public required int PendingCommandLine { get; init; }

    /// <summary>获取当前待处理连接生命周期状态数。</summary>
    public required int PendingLifecycleState { get; init; }

    /// <summary>获取当前等待完成 ACK Socket 发送的任务数。</summary>
    public required int PendingConfirmSend { get; init; }

    /// <summary>获取接收业务管线当前队列深度。</summary>
    public required int QueueDepth { get; init; }

    /// <summary>获取当前指标周期观察到的最大队列年龄，单位毫秒。</summary>
    public required long MaxQueueAgeMilliseconds { get; init; }

    /// <summary>获取当前指标周期 ACK 组帧到 Socket Send 返回的最大耗时，单位毫秒。</summary>
    public required long AckMaxElapsedMilliseconds { get; init; }

    /// <summary>获取当前指标周期 ACK Socket 发送完成最大耗时，单位毫秒。</summary>
    public required long AckSendCompletedMaxElapsedMilliseconds { get; init; }

    /// <summary>获取当前指标周期登录到业务线路可用的最大耗时，单位毫秒。</summary>
    public required long LoginBusinessReadyMaxElapsedMilliseconds { get; init; }

    /// <summary>获取命令线路启动是否因 ACK 压力保护而暂停，零表示未暂停。</summary>
    public required int CommandLineStartPaused { get; init; }

    /// <summary>获取命令线路专用管线当前队列深度。</summary>
    public required int CommandLineQueueDepth { get; init; }

    /// <summary>获取命令线路专用管线最大队列年龄，单位毫秒。</summary>
    public required long CommandLineMaxQueueAgeMilliseconds { get; init; }

    /// <summary>获取连接生命周期专用管线当前队列深度。</summary>
    public required int LifecycleQueueDepth { get; init; }

    /// <summary>获取连接生命周期专用管线最大队列年龄，单位毫秒。</summary>
    public required long LifecycleMaxQueueAgeMilliseconds { get; init; }
}

/// <summary>
/// 表示测试时间窗内 TcpCenter 的连接里程碑、业务积压峰值和累计计数增量。
/// </summary>
internal sealed record TcpCenterAnalysisSummary
{
    /// <summary>获取服务指标文件路径。</summary>
    public required string SourcePath { get; init; }

    /// <summary>获取服务指标样本数。</summary>
    public required int SampleCount { get; init; }

    /// <summary>获取测试期间观测到的最大物理连接数。</summary>
    public required int MaximumSocketConnections { get; init; }

    /// <summary>获取测试期间观测到的最大地址绑定数。</summary>
    public required int MaximumBoundDeviceConnections { get; init; }

    /// <summary>获取测试期间观测到的最大业务在线数。</summary>
    public required int MaximumBusinessOnlineDevices { get; init; }

    /// <summary>获取最大绑定但业务线路未建立差额。</summary>
    public required int MaximumBoundWithoutBusinessLine { get; init; }

    /// <summary>获取最大待处理登录数。</summary>
    public required int MaximumPendingLogin { get; init; }

    /// <summary>获取最大待处理心跳数。</summary>
    public required int MaximumPendingHeartbeat { get; init; }

    /// <summary>获取接收业务管线最大队列深度。</summary>
    public required long MaximumQueueDepth { get; init; }

    /// <summary>获取接收业务管线最大队列年龄，单位毫秒。</summary>
    public required long MaximumQueueAgeMilliseconds { get; init; }

    /// <summary>获取 ACK 最大耗时，单位毫秒。</summary>
    public required long MaximumAckElapsedMilliseconds { get; init; }

    /// <summary>获取 ACK Socket 发送完成最大耗时，单位毫秒。</summary>
    public required long MaximumAckSendCompletedElapsedMilliseconds { get; init; }

    /// <summary>获取登录到业务线路可用的最大耗时，单位毫秒。</summary>
    public required long MaximumLoginBusinessReadyElapsedMilliseconds { get; init; }

    /// <summary>获取最大待处理命令线路数。</summary>
    public required int MaximumPendingCommandLine { get; init; }

    /// <summary>获取最大待处理生命周期状态数。</summary>
    public required int MaximumPendingLifecycleState { get; init; }

    /// <summary>获取最大待完成 ACK 发送数。</summary>
    public required int MaximumPendingConfirmSend { get; init; }

    /// <summary>获取命令线路专用管线最大队列深度。</summary>
    public required long MaximumCommandLineQueueDepth { get; init; }

    /// <summary>获取命令线路专用管线最大队列年龄，单位毫秒。</summary>
    public required long MaximumCommandLineQueueAgeMilliseconds { get; init; }

    /// <summary>获取生命周期专用管线最大队列深度。</summary>
    public required long MaximumLifecycleQueueDepth { get; init; }

    /// <summary>获取生命周期专用管线最大队列年龄，单位毫秒。</summary>
    public required long MaximumLifecycleQueueAgeMilliseconds { get; init; }

    /// <summary>获取 ACK 压力保护暂停命令线路启动的最大持续时间，单位毫秒。</summary>
    public required long MaximumCommandLineStartPauseElapsedMilliseconds { get; init; }

    /// <summary>获取测试窗口内命令线路启动被压力保护暂停的次数增量。</summary>
    public required long CommandLineStartPauseCount { get; init; }

    /// <summary>获取测试窗口内 ACK 发送失败数量增量。</summary>
    public required long AckFailed { get; init; }

    /// <summary>获取测试窗口内 ACK 发送因连接失效等原因中止的数量增量。</summary>
    public required long AckSendAborted { get; init; }

    /// <summary>获取测试窗口内连接关闭数量增量。</summary>
    public required long ConnectionClosed { get; init; }

    /// <summary>获取测试窗口内带错误关闭的连接数量增量。</summary>
    public required long ConnectionCloseWithError { get; init; }

    /// <summary>获取测试窗口内接收侧关闭的连接数量增量。</summary>
    public required long ConnectionCloseReceive { get; init; }

    /// <summary>获取测试窗口内无法归入明确原因的其他连接关闭数量增量。</summary>
    public required long ConnectionCloseOther { get; init; }

    /// <summary>获取测试窗口内命令线路任务被拒绝的数量增量。</summary>
    public required long CommandLineRejected { get; init; }

    /// <summary>获取测试窗口内生命周期状态任务被拒绝的数量增量。</summary>
    public required long LifecycleStateRejected { get; init; }

    /// <summary>获取 Pipeline 固定 Worker 数量。</summary>
    public required int WorkerCount { get; init; }

    /// <summary>获取测试期间 Pipeline 处理失败增量。</summary>
    public required long PipelineFailed { get; init; }

    /// <summary>获取测试期间 Pipeline 拒绝增量。</summary>
    public required long PipelineRejected { get; init; }

    /// <summary>获取测试期间已 ACK 但业务管线未接管增量。</summary>
    public required long AckedButRejected { get; init; }

    /// <summary>获取测试期间慢业务发现旧会话并安全丢弃的增量。</summary>
    public required long StaleBusiness { get; init; }

    /// <summary>获取测试期间被合并的重复心跳增量。</summary>
    public required long HeartbeatCoalesced { get; init; }

    /// <summary>获取测试窗口内 TcpCenter 进程重启次数。</summary>
    public required int ProcessRestartCount { get; init; }

    /// <summary>获取物理连接首次达到目标的 UTC 时间；未达到时为空。</summary>
    public DateTimeOffset? PhysicalTargetReachedAtUtc { get; init; }

    /// <summary>获取业务在线首次达到目标的 UTC 时间；未达到时为空。</summary>
    public DateTimeOffset? BusinessTargetReachedAtUtc { get; init; }

    /// <summary>获取物理连接达到目标后业务在线追平耗时；任一里程碑缺失时为空。</summary>
    public double? BusinessCatchupSeconds { get; init; }
}

/// <summary>
/// 表示测试期间单台或多台 LoadTester 节点的资源峰值。
/// </summary>
internal sealed record LoadGeneratorResourceSummary
{
    /// <summary>获取最大 CPU 使用率百分比。</summary>
    public required double MaximumCpuPercent { get; init; }

    /// <summary>获取最大工作集字节数。</summary>
    public required long MaximumWorkingSetBytes { get; init; }

    /// <summary>获取最大托管堆字节数。</summary>
    public required long MaximumManagedHeapBytes { get; init; }

    /// <summary>获取最大线程池线程数。</summary>
    public required int MaximumThreadPoolThreads { get; init; }

    /// <summary>获取最大线程池排队工作项数。</summary>
    public required long MaximumPendingWorkItems { get; init; }
}

/// <summary>
/// 表示按阶段名称跨分片合并后的 HTTP 指令执行结果。
/// </summary>
internal sealed record DetailedCommandPhaseSummary
{
    /// <summary>获取阶段名称。</summary>
    public required string Name { get; init; }

    /// <summary>获取阶段是否同步等待设备业务结果。</summary>
    public required bool ShowResult { get; init; }

    /// <summary>获取阶段计划请求数。</summary>
    public required long PlannedRequests { get; init; }

    /// <summary>获取阶段实际发起请求数。</summary>
    public required long StartedRequests { get; init; }

    /// <summary>获取阶段业务成功请求数。</summary>
    public required long SucceededRequests { get; init; }

    /// <summary>获取阶段失败请求数。</summary>
    public required long FailedRequests { get; init; }

    /// <summary>获取阶段超时请求数。</summary>
    public required long TimeoutRequests { get; init; }

    /// <summary>获取明确未进入命令队列的数据预校验拒绝数。</summary>
    public required long PrevalidationRejected { get; init; }

    /// <summary>获取 API 估算已进入服务端命令链路的请求数。</summary>
    public required long EstimatedEnqueued { get; init; }

    /// <summary>获取阶段离线跳过请求数。</summary>
    public required long SkippedOfflineRequests { get; init; }

    /// <summary>获取阶段模拟设备收到的读表命令数。</summary>
    public required long CommandsReceived { get; init; }

    /// <summary>获取至少关联到一帧原始下行的唯一命令数，重复或重试帧不重复计数。</summary>
    public required long RawDownstreamCommands { get; init; }

    /// <summary>获取被当前模拟器识别为支持格式的唯一命令数。</summary>
    public required long RecognizedCommands { get; init; }

    /// <summary>获取收到但当前模拟器不支持应答格式的唯一命令数。</summary>
    public required long UnsupportedCommands { get; init; }

    /// <summary>获取按报文指纹识别出的重复或服务端重试下行帧数。</summary>
    public required long DuplicateDownstreamFrames { get; init; }

    /// <summary>获取阶段模拟设备成功应答的读表命令数。</summary>
    public required long CommandsResponded { get; init; }

    /// <summary>获取估算入队但排空窗口内未被设备观测到的命令数。</summary>
    public required long CommandsUnobserved { get; init; }

    /// <summary>获取原始下行唯一命令数除以估算入队数的交付率。</summary>
    public required double RawDeliveryRate { get; init; }

    /// <summary>获取协议已识别唯一命令数除以原始下行唯一命令数的识别率。</summary>
    public required double RecognitionRate { get; init; }

    /// <summary>获取协议已识别唯一命令数除以估算入队数的兼容交付率。</summary>
    public required double DeliveryRate { get; init; }

    /// <summary>获取协议已识别命令成功应答的比例。</summary>
    public required double ResponseRate { get; init; }

    /// <summary>获取跨分片排空是否全部通过；未启用排空时为空。</summary>
    public bool? DrainSatisfied { get; init; }

    /// <summary>获取各分片最长排空等待耗时，单位毫秒。</summary>
    public required long MaximumDrainWaitMilliseconds { get; init; }

    /// <summary>获取合并阶段 HTTP 延迟 P95，单位毫秒。</summary>
    public required long P95Milliseconds { get; init; }

    /// <summary>获取任一分片是否触发阶段保护。</summary>
    public required bool CircuitBreakerTripped { get; init; }

    /// <summary>获取阶段开始时间。</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>获取阶段结束时间。</summary>
    public required DateTimeOffset EndedAtUtc { get; init; }
}

/// <summary>
/// 定义详细报告验收项的三态结果，避免服务端文件缺失时被错误显示为通过。
/// </summary>
internal enum DetailedAcceptanceStatus
{
    /// <summary>指标满足阈值。</summary>
    Passed,

    /// <summary>指标不满足阈值。</summary>
    Failed,

    /// <summary>缺少必要数据，无法判定。</summary>
    Unavailable
}

/// <summary>
/// 表示详细报告中的一个可审计验收项。
/// </summary>
internal sealed record DetailedAcceptanceCheck
{
    /// <summary>获取验收项名称。</summary>
    public required string Name { get; init; }

    /// <summary>获取验收状态。</summary>
    public required DetailedAcceptanceStatus Status { get; init; }

    /// <summary>获取便于人工复核的实际值文本。</summary>
    public required string Actual { get; init; }

    /// <summary>获取门槛或预期值文本。</summary>
    public required string Expected { get; init; }

    /// <summary>获取判断依据或不可评估原因。</summary>
    public required string Evidence { get; init; }
}

/// <summary>
/// 定义自动诊断问题的严重程度。
/// </summary>
internal enum DiagnosticSeverity
{
    /// <summary>需要立即处理且会使关键验收无效的问题。</summary>
    P0,

    /// <summary>显著影响容量、延迟或稳定性的问题。</summary>
    P1,

    /// <summary>建议跟踪但未直接破坏本轮核心目标的问题。</summary>
    P2
}

/// <summary>
/// 定义自动诊断结论的证据置信度。
/// </summary>
internal enum DiagnosticConfidence
{
    /// <summary>由计数、时间线或明确状态直接证明。</summary>
    Fact,

    /// <summary>由多项相关指标强关联推断，仍需日志或性能跟踪最终确认。</summary>
    HighProbability,

    /// <summary>仅作为下一步排查方向，当前数据不足以确认。</summary>
    Hypothesis
}

/// <summary>
/// 表示一条带证据、影响、建议和置信度的自动诊断结果。
/// </summary>
internal sealed record DiagnosticFinding
{
    /// <summary>获取问题严重程度。</summary>
    public required DiagnosticSeverity Severity { get; init; }

    /// <summary>获取诊断标题。</summary>
    public required string Title { get; init; }

    /// <summary>获取直接指标事实或推断链路。</summary>
    public required string Evidence { get; init; }

    /// <summary>获取对测试结论或服务能力的影响。</summary>
    public required string Impact { get; init; }

    /// <summary>获取下一步修改或验证建议。</summary>
    public required string Recommendation { get; init; }

    /// <summary>获取结论置信度。</summary>
    public required DiagnosticConfidence Confidence { get; init; }
}

/// <summary>
/// 表示当前结果与可选基线之间的一个关键指标对比。
/// </summary>
internal sealed record ReportComparisonItem
{
    /// <summary>获取指标名称。</summary>
    public required string Name { get; init; }

    /// <summary>获取基线值文本。</summary>
    public required string Baseline { get; init; }

    /// <summary>获取当前值文本。</summary>
    public required string Current { get; init; }

    /// <summary>获取变化方向和幅度说明。</summary>
    public required string Change { get; init; }
}
