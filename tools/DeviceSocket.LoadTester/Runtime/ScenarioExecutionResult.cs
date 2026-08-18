using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 保存单个负载节点完成 TCP 场景后的时间范围、最终指标、阶段明细和取消状态，供节点报告统一落盘。
/// </summary>
/// <param name="StartedAtUtc">本节点开始执行场景的 UTC 时刻。</param>
/// <param name="EndedAtUtc">本节点结束场景测量的 UTC 时刻。</param>
/// <param name="Measurement">设备停止前采集的最终累计指标，避免正常清理连接改变最终在线数。</param>
/// <param name="Phases">按执行顺序记录的 TCP 阶段结果。</param>
/// <param name="Cancelled">是否因用户或宿主取消而提前结束。</param>
internal sealed record ScenarioExecutionResult(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    MetricSnapshot Measurement,
    IReadOnlyList<PhaseResult> Phases,
    bool Cancelled);
