namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 指定 HTTP 指令阶段排空超时后的后续行为，对应 <c>commandWorkload.phases[].drainFailureAction</c>。
/// </summary>
public enum CommandDrainFailureAction
{
    /// <summary>记录当前阶段失败，但继续执行下一阶段；适合探索容量上限，可能受到前序积压干扰。</summary>
    Continue,

    /// <summary>记录当前阶段失败并跳过紧随其后的一个阶段，避免积压继续放大。</summary>
    SkipNextPhase,

    /// <summary>记录当前阶段失败并终止整轮测试；最终报告仍会保留终止前数据。</summary>
    AbortRun
}
