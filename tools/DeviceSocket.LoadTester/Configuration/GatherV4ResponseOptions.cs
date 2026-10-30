namespace DeviceSocket.LoadTester.Configuration;

/// <summary>配置 <c>workload.gatherV4Response</c> 下 GatherV4 命令确认和关联故障模型。</summary>
public sealed class GatherV4ResponseOptions
{
    /// <summary>
    /// 配置 workload.gatherV4Response.maximumPendingResponsesPerDevice；默认 32，范围 1～1024，单位条；
    /// 限制单连接待发送的延迟应答，满时记录拒绝而不阻塞心跳；零无效，按连接周期清理，重启压测生效。
    /// </summary>
    public int MaximumPendingResponsesPerDevice { get; set; } = 32;

    /// <summary>
    /// 配置 workload.gatherV4Response.repeatObservationSeconds；默认 60，范围 1～3600，单位秒；
    /// 同一会话最近 64 条报文在此窗口内重复出现时记为疑似重发，不能据此断言同一业务 CommandId；零无效。
    /// </summary>
    public int RepeatObservationSeconds { get; set; } = 60;

    /// <summary>
    /// 获取或设置 <c>workload.gatherV4Response.enabled</c>；默认 true，false 表示识别命令但全部不应答，用于验证服务端超时恢复。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 获取或设置 <c>workload.gatherV4Response.mode</c>；默认 normal，决定正确 F1、deny 否认 F2、延迟、丢弃、重复、错误 A3 或错误 PSEQ 应答。
    /// </summary>
    public GatherV4ResponseMode Mode { get; set; } = GatherV4ResponseMode.Normal;

    /// <summary>
    /// 获取或设置 <c>workload.gatherV4Response.affectedDevicePercentage</c>；单位百分比，默认 100，范围 0～100；未命中的设备始终正常应答。
    /// </summary>
    public double AffectedDevicePercentage { get; set; } = 100;

    /// <summary>
    /// 获取或设置 <c>workload.gatherV4Response.delayMilliseconds</c>；单位毫秒，默认 1000，仅 delayed 模式生效，零表示立即应答。
    /// </summary>
    public int DelayMilliseconds { get; set; } = 1_000;
}
