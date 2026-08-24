namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示一个阶段内的目标设备比例、连接速率和持续时间。
/// </summary>
public sealed class ScenarioPhaseOptions
{
    /// <summary>
    /// 获取或设置 <c>scenario.phases[].name</c>；默认 <c>steady</c>，用于时间序列和报告定位阶段，空值无效。
    /// </summary>
    public string Name { get; set; } = "steady";

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].kind</c>；默认 <see cref="ScenarioPhaseKind.Steady"/>，用于报告分类，不会自动改变连接或故障参数。
    /// </summary>
    public ScenarioPhaseKind Kind { get; set; } = ScenarioPhaseKind.Steady;

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].durationSeconds</c>；单位秒，默认 60，表示目标规模和故障参数保持时间，零值无效。
    /// </summary>
    public int DurationSeconds { get; set; } = 60;

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].targetDevicePercentage</c>；单位百分比，默认 100，按全局设备空间计算目标连接数，零表示该阶段不要求设备在线。
    /// </summary>
    public double TargetDevicePercentage { get; set; } = 100;

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].connectionsPerSecond</c>；单位为所有分片合计连接数/秒，默认空表示继承 <c>connection.connectionsPerSecond</c>，零值无效。
    /// </summary>
    public int? ConnectionsPerSecond { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].expectedServerRestart</c>；默认 false，true 表示该阶段预期服务端进程恰好发生一次计划重启，供详细报告区分计划操作和异常退出。
    /// </summary>
    public bool ExpectedServerRestart { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].manualAction</c>；默认禁用，启用后在阶段负载开始前显示提示并等待固定的准备与操作时间。
    /// </summary>
    public ManualActionOptions ManualAction { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].faults</c>；默认全部故障参数为零，阶段进入时替换当前故障快照，离开后由下一阶段配置覆盖。
    /// </summary>
    public FaultInjectionOptions Faults { get; set; } = new();
}
