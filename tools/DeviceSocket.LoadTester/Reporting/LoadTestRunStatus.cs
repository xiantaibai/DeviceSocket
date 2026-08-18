namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示单节点负载测试的执行状态；它与验收结果分离，用于区分指标未达标、阶段被跳过、用户取消和运行异常。
/// </summary>
public enum LoadTestRunStatus
{
    /// <summary>
    /// 所有计划阶段均完成且自动验收通过。
    /// </summary>
    Passed,

    /// <summary>
    /// 所有计划阶段均完成，但至少一条自动验收规则未通过。
    /// </summary>
    Failed,

    /// <summary>
    /// TCP 场景完成，但至少一个 HTTP 指令阶段因在线门槛等前置条件未满足而跳过。
    /// </summary>
    CompletedWithSkippedPhases,

    /// <summary>
    /// 用户通过 Ctrl+C 或外部取消令牌主动停止测试，报告包含取消前的累计指标。
    /// </summary>
    Cancelled,

    /// <summary>
    /// 测试因未处理运行异常提前结束，报告包含异常发生前的累计指标和失败原因。
    /// </summary>
    Aborted
}
