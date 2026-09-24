namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示 HTTP 指令压测中的一个限速、限并发执行阶段。
/// </summary>
public sealed class CommandPhaseOptions
{
    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].name</c>；用于控制台和报告定位阶段，默认空值无效。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].targetDeviceCount</c>；单位为全局设备数，默认 0 表示本轮全部设备；
    /// <c>targetDeviceSource=onlineRegistry</c> 时该值表示从当前在线注册表选取的数量，不限定为固定序号前缀。
    /// </summary>
    public int TargetDeviceCount { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].requestsPerDevice</c>；默认 1，同一设备大于 1 时用于制造连续多命令排队。
    /// </summary>
    public int RequestsPerDevice { get; set; } = 1;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].requestsPerSecond</c>；单位为所有分片合计请求数/秒，默认 10，零值无效。
    /// </summary>
    public int RequestsPerSecond { get; set; } = 10;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].maxConcurrency</c>；单位为所有分片合计在途请求数，默认 10，零值无效。
    /// </summary>
    public int MaxConcurrency { get; set; } = 10;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].showResult</c>；默认 false；旧 GatherTcpCenter 中 true 表示同步等待设备结果，
    /// GatherV4 的 IssueCmd 固定为同步接口，因此该兼容配置下必须设置为 true。同步等待会显著增加服务端订阅和 HTTP 在途请求压力。
    /// </summary>
    public bool ShowResult { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].pauseAfterSeconds</c>；单位秒，默认 0，阶段结束后等待队列回落，零表示立即进入下一阶段。
    /// </summary>
    public int PauseAfterSeconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].minimumOnlineCount</c>；单位为当前分片对应的全局设备数，默认空表示按
    /// <c>commandWorkload.requiredOnlineRate</c> 计算门槛，零表示当前阶段不要求设备在线，不能大于当前阶段目标数。
    /// <c>commandWorkload.targetDeviceSource=onlineRegistry</c> 时本节点任意在线设备都可用于满足该门槛；
    /// 多机分片运行时工具会按全局设备序号换算为当前分片应达到的本地在线数。
    /// </summary>
    public int? MinimumOnlineCount { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].onlineStableSeconds</c>；单位秒，默认空表示继承
    /// <c>commandWorkload.onlineStableSeconds</c>，零表示达到在线数后立即放行当前阶段。
    /// </summary>
    public int? OnlineStableSeconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].onlineWaitTimeoutSeconds</c>；单位秒，默认空表示继承
    /// <c>commandWorkload.onlineWaitTimeoutSeconds</c>，配置值必须大于零；超时只影响当前 HTTP 阶段，不会阻止 TCP 场景收尾和报告生成。
    /// </summary>
    public int? OnlineWaitTimeoutSeconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].onlineGateFailureAction</c>；默认 <see cref="OnlineGateFailureAction.SkipPhase"/>，
    /// 用于决定在线门槛超时后跳过当前阶段、带风险继续执行，还是终止整轮测试。
    /// </summary>
    public OnlineGateFailureAction OnlineGateFailureAction { get; set; } = OnlineGateFailureAction.SkipPhase;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].drainEnabled</c>；默认 false 保持旧场景行为，true 表示请求发送完毕后等待设备侧命令收到和应答指标追平。
    /// </summary>
    public bool DrainEnabled { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].minimumRawDeliveryRate</c>；取值 0～1，默认 0.95，
    /// 按“至少收到一帧关联下行的唯一命令数 / API 估算已入队数”计算；未知协议帧也算原始下行到达，
    /// 只在 <see cref="DrainEnabled"/> 为 true 且启用下行诊断时生效。
    /// </summary>
    public double MinimumRawDeliveryRate { get; set; } = 0.95;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].minimumDeliveryRate</c>；取值 0～1，默认 0.95，
    /// 按“排空窗口内被识别为支持命令的唯一请求数 / API 估算已入队数”计算；该字段保留原名称以兼容旧场景，
    /// 报告中明确展示为协议识别率；只在 <see cref="DrainEnabled"/> 为 true 时生效。
    /// </summary>
    public double MinimumDeliveryRate { get; set; } = 0.95;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].minimumResponseRate</c>；取值 0～1，默认 0.99，
    /// 按“设备完成应答的唯一命令数 / 协议已识别唯一命令数”计算；只在 <see cref="DrainEnabled"/> 为 true 时生效。
    /// </summary>
    public double MinimumResponseRate { get; set; } = 0.99;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].drainTimeoutSeconds</c>；单位秒，默认 600，必须大于零，表示等待业务队列追平的最长时间。
    /// </summary>
    public int DrainTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].quietSeconds</c>；单位秒，默认 10，达到交付和应答门槛后指标还需连续不变化该时长才认为排空，零表示达到门槛立即完成。
    /// </summary>
    public int QuietSeconds { get; set; } = 10;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].drainFailureAction</c>；默认 <see cref="CommandDrainFailureAction.Continue"/>，决定排空超时后继续、跳过下一阶段或终止测试。
    /// </summary>
    public CommandDrainFailureAction DrainFailureAction { get; set; } = CommandDrainFailureAction.Continue;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].minimumApiSuccessRate</c>；取值 0～1，默认空表示不做阶段级 HTTP 成功率验收；
    /// <c>showResult=false</c> 表示 API 接受率，<c>showResult=true</c> 表示同步结果成功率。
    /// </summary>
    public double? MinimumApiSuccessRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].maximumP95Milliseconds</c>；单位毫秒，默认空表示不做阶段级 P95 延迟验收，设置值必须大于零。
    /// </summary>
    public long? MaximumP95Milliseconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].circuitBreakerEnabled</c>；默认 false，启用后可按连续失败、超时率或最大阶段时长停止继续生产请求。
    /// </summary>
    public bool CircuitBreakerEnabled { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].maximumDurationSeconds</c>；单位秒，默认空表示不限制阶段生产时长，设置值必须大于零。
    /// 已经进入本地有界队列的少量请求仍会正常完成。
    /// </summary>
    public int? MaximumDurationSeconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].maximumConsecutiveFailures</c>；默认 0 表示不按连续失败熔断，大于零时达到该次数停止生产新请求。
    /// </summary>
    public int MaximumConsecutiveFailures { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].maximumTimeoutRate</c>；取值 0～1，默认空表示不按超时率熔断，按已发起请求中的超时比例计算。
    /// </summary>
    public double? MaximumTimeoutRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].circuitBreakerMinimumRequests</c>；单位请求数，默认 20，
    /// 超时率至少积累该数量样本后才参与熔断，避免首个偶发超时误停阶段。
    /// </summary>
    public int CircuitBreakerMinimumRequests { get; set; } = 20;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].circuitBreakerAction</c>；默认 <see cref="CommandCircuitBreakerAction.StopPhase"/>，决定仅停止当前阶段或终止整轮测试。
    /// </summary>
    public CommandCircuitBreakerAction CircuitBreakerAction { get; set; } = CommandCircuitBreakerAction.StopPhase;
}
