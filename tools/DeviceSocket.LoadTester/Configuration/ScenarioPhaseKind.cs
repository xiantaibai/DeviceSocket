namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 标识负载测试阶段的业务目的，便于报告区分预热、故障和恢复窗口。
/// </summary>
public enum ScenarioPhaseKind
{
    Warmup,
    Ramp,
    Steady,
    Fault,
    Recovery,
    Cooldown
}
