namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示一条 HTTP 指令在 API、下行识别和模拟应答链路上的最终状态。
/// </summary>
public enum CommandTraceOutcome
{
    /// <summary>命令尚未形成最终诊断结论。</summary>
    Pending,

    /// <summary>API 在进入命令队列前明确拒绝。</summary>
    PrevalidationRejected,

    /// <summary>估算已入队，但排空结束前没有关联到任何原始下行帧。</summary>
    NoDownstreamObserved,

    /// <summary>已经收到下行帧，但当前模拟器不支持该协议格式。</summary>
    UnsupportedDownstream,

    /// <summary>已经识别为支持的读表命令，但未成功写出业务响应。</summary>
    RecognizedWithoutResponse,

    /// <summary>已经识别命令并成功写出至少一条业务响应。</summary>
    Responded,

    /// <summary>HTTP 调用失败且没有观察到设备下行。</summary>
    HttpFailedWithoutDownstream,

    /// <summary>HTTP 返回失败，但设备侧仍观察到下行，通常表示同步结果超时或结果链路失败。</summary>
    HttpFailedAfterDownstream
}
