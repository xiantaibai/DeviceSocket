namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示进入场景阶段时需要操作人员执行的手工动作及固定准备窗口，主要用于服务端重启场景。
/// </summary>
public sealed class ManualActionOptions
{
    /// <summary>
    /// 获取或设置 <c>scenario.phases[].manualAction.enabled</c>；默认 false，true 时在阶段正式保持计时前显示操作提示并等待准备与操作窗口。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].manualAction.message</c>；默认提示重启服务端，显示在协调节点控制台，空值无效。
    /// </summary>
    public string Message { get; set; } = "请准备重启被测服务端。";

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].manualAction.preparationSeconds</c>；单位秒，默认 5，表示提示出现后到正式操作窗口开始前的倒计时，零表示立即开始。
    /// </summary>
    public int PreparationSeconds { get; set; } = 5;

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].manualAction.operationSeconds</c>；单位秒，默认 20，表示留给操作人员停止并启动服务的固定窗口，零值无效。
    /// </summary>
    public int OperationSeconds { get; set; } = 20;

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].manualAction.coordinatorOnlyPrompt</c>；默认 true，多分片时仅分片 0 输出提示，所有分片仍等待相同时间保持阶段同步。
    /// </summary>
    public bool CoordinatorOnlyPrompt { get; set; } = true;
}
