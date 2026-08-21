namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 表示一条接收记录与先前发送请求的关联结果。
/// </summary>
/// <param name="Category">关联修正后的最终分类，例如心跳确认会继承为心跳类别。</param>
/// <param name="StatusText">适合界面展示的匹配状态和响应耗时。</param>
/// <param name="ResponseTime">匹配成功时的响应耗时；无法匹配时为空。</param>
/// <param name="IsMatched">是否成功找到对应的发送请求。</param>
public sealed record DebugTrafficCorrelationResult(
    DebugTrafficCategory Category,
    string StatusText,
    TimeSpan? ResponseTime,
    bool IsMatched);
