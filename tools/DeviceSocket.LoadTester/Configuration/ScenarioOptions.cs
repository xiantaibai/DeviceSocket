namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示测试名称、默认持续时间以及可选的多阶段负载编排。
/// </summary>
public sealed class ScenarioOptions
{
    /// <summary>
    /// 获取或设置 <c>scenario.name</c>；默认 <c>smoke</c>，用于控制台、节点报告和汇总报告标识测试目的，空值无效。
    /// </summary>
    public string Name { get; set; } = "smoke";

    /// <summary>
    /// 获取或设置 <c>scenario.durationSeconds</c>；单位秒，默认 60，仅在 <see cref="Phases"/> 为空时作为单一稳态阶段时长，零值无效。
    /// </summary>
    public int DurationSeconds { get; set; } = 60;

    /// <summary>
    /// 获取或设置 <c>scenario.phases</c>；按列表顺序编排 TCP 规模和故障阶段，默认空表示使用 <see cref="DurationSeconds"/> 执行单一全量阶段。
    /// </summary>
    public List<ScenarioPhaseOptions> Phases { get; set; } = [];
}
