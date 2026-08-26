using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;
using System.Text.Json.Serialization;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示单个负载分片完整、可追溯且可参与合并的最终报告。
/// </summary>
public sealed record NodeReport
{
    /// <summary>获取节点报告结构版本；版本 3 新增逐请求命令关联、原始下行分类和未知报文诊断产物。</summary>
    public int SchemaVersion { get; init; } = 3;
    /// <summary>获取整轮分布式测试共享的运行编号，对应 <c>distributed.runId</c>。</summary>
    public required string RunId { get; init; }
    /// <summary>获取执行当前分片的负载机名称。</summary>
    public required string NodeName { get; init; }
    /// <summary>获取当前分片从零开始的编号。</summary>
    public required int ShardIndex { get; init; }
    /// <summary>获取整轮测试期望参与的分片总数。</summary>
    public required int ShardCount { get; init; }
    /// <summary>获取当前分片实际分配的虚拟设备数量。</summary>
    public required int LocalDeviceCount { get; init; }
    /// <summary>获取当前节点开始执行测试的 UTC 时间。</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }
    /// <summary>获取当前节点完成、取消或异常结束的 UTC 时间。</summary>
    public required DateTimeOffset EndedAtUtc { get; init; }
    /// <summary>获取解析默认值和命令行覆盖后的完整配置快照；鉴权密钥只保存环境变量名，不保存密钥值。</summary>
    public required LoadTestOptions Options { get; init; }
    /// <summary>获取停止设备连接前保留的累计业务指标；异常收尾时为报告生成前最后可用快照。</summary>
    public required MetricSnapshot Metrics { get; init; }
    /// <summary>获取报告生成时负载机进程的 CPU、内存、线程和句柄资源快照。</summary>
    public required ResourceSnapshot Resources { get; init; }
    /// <summary>获取可选服务端状态接口的基线和峰值汇总；未启用服务端观测时为空。</summary>
    public GatewayObservationSummary? GatewayObservation { get; init; }
    /// <summary>获取已经完成的 TCP 场景阶段及其阶段结束指标；异常发生前尚未完成的阶段不会伪造结果。</summary>
    public required IReadOnlyList<PhaseResult> Phases { get; init; }
    /// <summary>
    /// 获取当前节点依次完成的 HTTP 指令阶段及其增量指标；纯设备角色为空。
    /// </summary>
    public IReadOnlyList<CommandPhaseResult> CommandPhases { get; init; } = [];
    /// <summary>获取当前节点 HTTP 指令失败明细；成功请求不会逐条保存，避免一万台测试报告无界膨胀。</summary>
    public IReadOnlyList<HttpRequestErrorDetail> HttpRequestErrors { get; init; } = [];
    /// <summary>获取本节点下行原始交付、协议识别、应答、重复帧与未关联帧汇总；未启用诊断时为空。</summary>
    public DownstreamDiagnosticSummary? DownstreamDiagnostics { get; init; }
    /// <summary>获取逐 HTTP 请求关联结果；单独写入 JSON Lines，不内嵌节点摘要以控制文件大小。</summary>
    [JsonIgnore]
    public IReadOnlyList<CommandTraceDetail> CommandTraces { get; init; } = [];
    /// <summary>获取按指纹限量保存的未知或不支持下行样本；单独写入 JSON Lines，不内嵌节点摘要。</summary>
    [JsonIgnore]
    public IReadOnlyList<DownstreamDiagnosticSample> DownstreamDiagnosticSamples { get; init; } = [];
    /// <summary>获取自动验收规则和最终通过标志；执行异常或阶段跳过还需结合 <see cref="RunStatus"/> 判断。</summary>
    public required AcceptanceResult Acceptance { get; init; }
    /// <summary>获取资源预检期间发现但未阻止测试启动的警告。</summary>
    public required IReadOnlyList<string> PreflightWarnings { get; init; }
    /// <summary>获取当前节点执行状态；旧版本报告缺少该字段时按默认 <see cref="LoadTestRunStatus.Failed"/> 处理。</summary>
    public LoadTestRunStatus RunStatus { get; init; } = LoadTestRunStatus.Failed;
    /// <summary>获取取消或异常终止的中文原因；正常完成以及仅阶段跳过时为空。</summary>
    public string? FailureReason { get; init; }
}
