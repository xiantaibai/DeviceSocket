namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示节点报告目录、采样周期和控制台输出策略。
/// </summary>
public sealed class ReportingOptions
{
    /// <summary>
    /// 获取或设置 <c>reporting.outputDirectory</c>；默认 <c>load-test-results</c>，每轮测试会在其下按 RunId 和分片号创建独立目录。
    /// 相对路径以进程工作目录为基准，空值无效。
    /// </summary>
    public string OutputDirectory { get; set; } = "load-test-results";

    /// <summary>
    /// 获取或设置 <c>reporting.sampleIntervalSeconds</c>；单位秒，默认 1，控制客户端资源、连接指标和可选服务端状态写入
    /// <c>node-timeseries.jsonl</c> 的周期，零值无效；周期过短会增加负载机磁盘写入压力。
    /// </summary>
    public int SampleIntervalSeconds { get; set; } = 1;

    /// <summary>
    /// 获取或设置 <c>reporting.checkpointIntervalSeconds</c>；单位秒，默认 10，周期覆盖写入 <c>run-checkpoint.json</c>，
    /// 供进程异常结束后恢复最后一份累计指标；必须不小于采样周期，零值无效。
    /// </summary>
    public int CheckpointIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// 获取或设置 <c>reporting.writeConsoleSummary</c>；默认 true，每次采样向控制台输出简要指标，false 仅关闭控制台摘要，不影响报告文件。
    /// </summary>
    public bool WriteConsoleSummary { get; set; } = true;

    /// <summary>
    /// 获取或设置 <c>reporting.downstreamDiagnostics</c>；定义 HTTP 请求与设备下行帧关联、未知协议采样和诊断文件大小上限。
    /// </summary>
    public DownstreamDiagnosticsOptions DownstreamDiagnostics { get; set; } = new();
}
