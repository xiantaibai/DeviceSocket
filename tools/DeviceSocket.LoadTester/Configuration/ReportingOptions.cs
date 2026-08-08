namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示节点报告目录、采样周期和控制台输出策略。
/// </summary>
public sealed class ReportingOptions
{
    public string OutputDirectory { get; set; } = "load-test-results";
    public int SampleIntervalSeconds { get; set; } = 1;
    public bool WriteConsoleSummary { get; set; } = true;
}
