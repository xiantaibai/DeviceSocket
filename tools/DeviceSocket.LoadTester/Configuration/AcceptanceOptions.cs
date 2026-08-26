namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示测试结束时用于自动判定成功或失败的可选质量门槛。
/// </summary>
public sealed class AcceptanceOptions
{
    /// <summary>
    /// 获取或设置 <c>acceptance.usePreRestartPhaseRates</c>；默认 false 使用整轮累计连接/登录成功率，
    /// true 时只统计首次计划重启前的预热、爬升和稳态阶段，避免预期重连风暴污染初始登录质量验收。
    /// </summary>
    public bool UsePreRestartPhaseRates { get; set; }

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
    /// 获取或设置 <c>acceptance.maximumReconnects</c>；单位为所有分片合计累计重连次数，默认空表示不验收，用于阻止仅满足最小重连数但发生级联重连的场景通过。
    /// </summary>
    public long? MaximumReconnects { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.maximumReconnectsPerDevice</c>；默认空表示不验收，按累计重连次数除以目标设备数计算，零值允许完全不重连。
    /// </summary>
    public double? MaximumReconnectsPerDevice { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.maximumSecondaryOutages</c>；单位为掉线事件数，默认空表示不验收，不包含计划重启阶段内首次被标记为计划重启的掉线事件。
    /// </summary>
    public int? MaximumSecondaryOutages { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumStableOnlineSeconds</c>；单位秒，默认空表示不验收，要求测量快照时已经连续保持完整目标在线指定时长。
    /// </summary>
    public double? MinimumStableOnlineSeconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumFinalOnlineCount</c>；单位为全部分片合计设备数，默认空表示不验收，多节点汇总时按节点在线数求和。
    /// </summary>
    public int? MinimumFinalOnlineCount { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.maximumFinalOnlineCount</c>；单位为全部分片合计设备数，默认空表示不验收，常用于确认某阶段只保持预期规模。
    /// </summary>
    public int? MaximumFinalOnlineCount { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumReconnects</c>；单位为所有分片合计累计重连次数，默认空表示不验收，零表示允许没有发生重连；节点运行时按全局序号分摊，汇总报告按全局值验收。
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
    /// 获取或设置 <c>acceptance.minimumApiTargetCoverageRate</c>；取值 0～1，默认空表示不验收，
    /// 按实际发起 HTTP 请求数除以“已发起请求数 + 因设备离线跳过请求数”计算，防止只统计少量在线设备时业务成功率虚高。
    /// </summary>
    public double? MinimumApiTargetCoverageRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumGatherReadingResponseRate</c>；取值 0～1，默认空表示不验收，按模拟终端已应答读表命令数除以收到的读表命令数计算。
    /// </summary>
    public double? MinimumGatherReadingResponseRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.maximumApiP95Milliseconds</c>；单位毫秒，默认空表示不验收，限制 HTTP 请求端到端延迟第 95 百分位，零值无效。
    /// </summary>
    public long? MaximumApiP95Milliseconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumAsyncApiAcceptanceRate</c>；取值 0～1，默认空表示不验收，
    /// 只汇总 <c>showResult=false</c> 阶段的 HTTP 业务成功数除以发起数。
    /// </summary>
    public double? MinimumAsyncApiAcceptanceRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumSyncResultSuccessRate</c>；取值 0～1，默认空表示不验收，
    /// 只汇总 <c>showResult=true</c> 阶段的同步业务成功数除以发起数。
    /// </summary>
    public double? MinimumSyncResultSuccessRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumCommandDeliveryRate</c>；取值 0～1，默认空表示不验收，
    /// 按全部已执行 HTTP 阶段的原始下行唯一命令数除以 API 估算已入队数计算，未知协议格式也计为已交付。
    /// </summary>
    public double? MinimumCommandDeliveryRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.minimumCommandRecognitionRate</c>；取值 0～1，默认空表示不验收，
    /// 按协议已识别唯一命令数除以原始下行唯一命令数计算，用于把“服务端未下发”和“模拟器未兼容报文”分开判定。
    /// </summary>
    public double? MinimumCommandRecognitionRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>acceptance.maximumUnobservedCommands</c>；单位命令数，默认空表示不验收，
    /// 表示 API 估算已入队但在阶段排空窗口内没有关联到任何原始下行的最大允许数量，零表示不允许缺口。
    /// </summary>
    public long? MaximumUnobservedCommands { get; set; }
}
