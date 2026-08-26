namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 指定 HTTP 指令阶段触发保护阈值后的行为，对应 <c>commandWorkload.phases[].circuitBreakerAction</c>。
/// </summary>
public enum CommandCircuitBreakerAction
{
    /// <summary>停止继续生产当前阶段请求，等待已经进入本地队列的请求结束，然后继续后续阶段。</summary>
    StopPhase,

    /// <summary>停止当前阶段并终止整轮测试；最终报告仍会保留触发原因和已完成请求。</summary>
    AbortRun
}
