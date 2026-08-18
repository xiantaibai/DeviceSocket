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
    /// 获取或设置 <c>commandWorkload.phases[].targetDeviceCount</c>；单位为全局设备数，默认 0 表示本轮全部设备。
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
    /// 获取或设置 <c>commandWorkload.phases[].showResult</c>；默认 false，true 会让老 API 同步等待设备业务结果并显著增加服务端订阅和请求线程压力。
    /// </summary>
    public bool ShowResult { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].pauseAfterSeconds</c>；单位秒，默认 0，阶段结束后等待队列回落，零表示立即进入下一阶段。
    /// </summary>
    public int PauseAfterSeconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.phases[].minimumOnlineCount</c>；单位为当前分片对应的全局设备数，默认空表示按
    /// <c>commandWorkload.requiredOnlineRate</c> 计算门槛，零表示当前阶段不要求设备在线，不能大于 <c>devices.count</c>。
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
}
