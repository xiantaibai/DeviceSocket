namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 标识负载测试阶段的业务目的，便于报告区分预热、故障和恢复窗口。
/// </summary>
public enum ScenarioPhaseKind
{
    /// <summary>
    /// 少量设备预热阶段，用于验证目标地址、协议登录和监控链路是否可用。
    /// </summary>
    Warmup,

    /// <summary>
    /// 逐步扩大在线规模的爬升阶段，用于观察容量拐点。
    /// </summary>
    Ramp,

    /// <summary>
    /// 保持目标规模的稳态阶段，用于测量长连接和指令吞吐稳定性。
    /// </summary>
    Steady,

    /// <summary>
    /// 应用丢包、半包、断线等故障的阶段，用于验证异常隔离和自恢复。
    /// </summary>
    Fault,

    /// <summary>
    /// 故障撤销后的恢复阶段，用于测量重连耗时和最终在线率。
    /// </summary>
    Recovery,

    /// <summary>
    /// 负载结束前的冷却阶段，用于观察队列和资源是否回落。
    /// </summary>
    Cooldown
}
