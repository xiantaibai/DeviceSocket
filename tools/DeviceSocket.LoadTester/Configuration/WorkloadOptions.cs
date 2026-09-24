namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示除协议心跳之外的周期设备上报负载。
/// </summary>
public sealed class WorkloadOptions
{
    /// <summary>
    /// 获取或设置 <c>workload.reportsEnabled</c>；默认 false，表示不发送协议专属周期业务上报。
    /// </summary>
    public bool ReportsEnabled { get; set; }

    /// <summary>
    /// 获取或设置 <c>workload.reportIntervalSeconds</c>；单位秒，默认 300，启用周期上报时必须大于零。
    /// </summary>
    public int ReportIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// 获取或设置 <c>workload.jitterSeconds</c>；单位秒，默认 10，用于打散周期上报且必须小于上报间隔，零表示不抖动。
    /// </summary>
    public int JitterSeconds { get; set; } = 10;

    /// <summary>
    /// 获取或设置 <c>workload.gatherReadingResponse</c>；定义 Gather 老服务 AFN=0CH/F129 读表指令的终端应答与故障行为。
    /// </summary>
    public GatherReadingResponseOptions GatherReadingResponse { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>workload.gatherV4Response</c>；定义 GatherV4 下行命令按 A3/PSEQ 应答时的延迟、丢弃、重复和错误关联故障模型。
    /// </summary>
    public GatherV4ResponseOptions GatherV4Response { get; set; } = new();

    /// <summary>
    /// 获取或设置 <c>workload.gatherV4Report</c>；定义 GatherV4 周期主动上报的真实协议帧模板和 ACK 请求标志。
    /// </summary>
    public GatherV4ReportOptions GatherV4Report { get; set; } = new();
}
