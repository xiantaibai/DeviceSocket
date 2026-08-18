namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示测试结束时用于自动判定成功或失败的可选质量门槛。
/// </summary>
public sealed class AcceptanceOptions
{
    /// <summary>
    /// 获取或设置 <c>acceptance.minimumConnectionSuccessRate</c>；取值 0～1，默认空表示不验收，按 TCP 建连成功次数除以尝试次数计算。
    /// </summary>
    public double? MinimumConnectionSuccessRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumLoginSuccessRate</c>；取值 0～1，默认空表示不验收，按协议登录确认次数除以登录发送次数计算。
    /// </summary>
    public double? MinimumLoginSuccessRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumFinalOnlineRate</c>；取值 0～1，默认 0.95，按测试结束时在线设备数除以本分片设备数计算，空值表示不验收。
    /// </summary>
    public double? MinimumFinalOnlineRate { get; set; } = 0.95;

    /// <summary>
    /// 获取或设置 <c>acceptance.maximumProtocolErrorRate</c>；取值 0～1，默认 0.01，按非法协议帧数除以接收帧数计算，空值表示不验收。
    /// </summary>
    public double? MaximumProtocolErrorRate { get; set; } = 0.01;

    /// <summary>
    /// 获取或设置 <c>acceptance.maximumRecoverySeconds</c>；单位秒，默认空表示不验收，衡量故障结束后恢复目标在线规模的最长时间，零值无效。
    /// </summary>
    public double? MaximumRecoverySeconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumFinalOnlineCount</c>；单位为全部分片合计设备数，默认空表示不验收，多节点汇总时按节点在线数求和。
    /// </summary>
    public int? MinimumFinalOnlineCount { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.maximumFinalOnlineCount</c>；单位为全部分片合计设备数，默认空表示不验收，常用于确认某阶段只保持预期规模。
    /// </summary>
    public int? MaximumFinalOnlineCount { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumReconnects</c>；单位为累计重连次数，默认空表示不验收，零表示允许没有发生重连。
    /// </summary>
    public long? MinimumReconnects { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumObservedAuthenticatedDevices</c>；单位设备数，默认空表示不验收，仅启用服务端观测时有意义。
    /// </summary>
    public int? MinimumObservedAuthenticatedDevices { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.maximumObservedAuthenticatedDevices</c>；单位设备数，默认空表示不验收，用于限制服务端认证设备数相对基线的增量。
    /// </summary>
    public int? MaximumObservedAuthenticatedDevices { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumApiSuccessRate</c>；取值 0～1，默认空表示不验收，按 HTTP 2xx 且业务返回成功的请求数除以总请求数计算。
    /// </summary>
    public double? MinimumApiSuccessRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumGatherReadingResponseRate</c>；取值 0～1，默认空表示不验收，按模拟终端已应答读表命令数除以收到的读表命令数计算。
    /// </summary>
    public double? MinimumGatherReadingResponseRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.maximumApiP95Milliseconds</c>；单位毫秒，默认空表示不验收，限制 HTTP 请求端到端延迟第 95 百分位，零值无效。
    /// </summary>
    public long? MaximumApiP95Milliseconds { get; set; }
}
