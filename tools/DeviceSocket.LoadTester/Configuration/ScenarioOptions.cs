namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示测试名称、默认持续时间以及可选的多阶段负载编排。
/// </summary>
public sealed class ScenarioOptions
{
    public string Name { get; set; } = "smoke";
    public int DurationSeconds { get; set; } = 60;
    public List<ScenarioPhaseOptions> Phases { get; set; } = [];
}
