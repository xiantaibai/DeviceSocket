namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示单个 HTTP 指令阶段的最终执行状态，用于区分真实执行、在线门槛跳过和异常失败。
/// </summary>
public enum CommandPhaseStatus
{
    /// <summary>
    /// 阶段已经实际发起全部计划请求并完成阶段后等待。
    /// </summary>
    Completed,

    /// <summary>
    /// 阶段因在线门槛等前置条件不满足而未发起请求；报告会保留跳过原因。
    /// </summary>
    Skipped,

    /// <summary>
    /// 阶段已经开始但因不可恢复异常未能完成。
    /// </summary>
    Failed
}
