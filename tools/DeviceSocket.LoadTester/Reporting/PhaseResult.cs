using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示一个场景阶段的配置、起止时间及阶段结束指标。
/// </summary>
/// <param name="Name">阶段名称。</param>
/// <param name="Kind">阶段业务类型。</param>
/// <param name="StartedAtUtc">阶段开始 UTC 时间，包含可选人工操作准备窗口。</param>
/// <param name="EndedAtUtc">阶段结束 UTC 时间。</param>
/// <param name="ExpectedOnline">当前分片在本阶段的目标在线设备数。</param>
/// <param name="Metrics">阶段结束时的累计指标快照。</param>
/// <param name="StartedMetrics">阶段开始时的累计指标快照；旧版报告反序列化时可为空。</param>
/// <param name="Delta">本阶段新增指标；旧版报告反序列化时可为空。</param>
/// <param name="MinimumProtocolOnline">阶段内最低协议在线数。</param>
/// <param name="MaximumProtocolOnline">阶段内最高协议在线数。</param>
/// <param name="ExpectedServerRestart">是否把本阶段首次中断视为计划内服务端重启。</param>
/// <param name="ActionPromptedAtUtc">人工操作提示显示时间；未启用人工操作时为空。</param>
/// <param name="ActionWindowStartedAtUtc">人工操作窗口开始时间；未启用人工操作时为空。</param>
/// <param name="ActionWindowEndedAtUtc">人工操作窗口结束时间；未启用人工操作时为空。</param>
public sealed record PhaseResult(
    string Name,
    ScenarioPhaseKind Kind,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    int ExpectedOnline,
    MetricSnapshot Metrics,
    MetricSnapshot? StartedMetrics = null,
    PhaseMetricDelta? Delta = null,
    long MinimumProtocolOnline = 0,
    long MaximumProtocolOnline = 0,
    bool ExpectedServerRestart = false,
    DateTimeOffset? ActionPromptedAtUtc = null,
    DateTimeOffset? ActionWindowStartedAtUtc = null,
    DateTimeOffset? ActionWindowEndedAtUtc = null);
