namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 定义 HTTP 指令阶段等待设备稳定在线失败后的处理策略。
/// </summary>
public enum OnlineGateFailureAction
{
    /// <summary>
    /// 跳过当前 HTTP 指令阶段并继续执行后续阶段；这是默认值，可确保在线容量不足时仍能完成 TCP 场景并生成报告。
    /// </summary>
    SkipPhase,

    /// <summary>
    /// 忽略在线门槛并继续执行当前阶段；仅用于明确接受向未在线设备发起业务请求的诊断场景。
    /// </summary>
    RunPhase,

    /// <summary>
    /// 终止本轮测试；用于需要保持旧版“在线不足立即失败”行为的兼容场景。
    /// </summary>
    AbortRun
}
