namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示一次 HTTP 指令失败的可追溯明细，用于区分数据预校验、业务结果、传输异常和超时。
/// </summary>
public sealed record HttpRequestErrorDetail
{
    /// <summary>获取当前设备配置的 GatherV4 故障应答模式；空表示正常。只说明注入背景，不能据此认定 HTTP 失败完全由注入引起。</summary>
    public string? ConfiguredDeviceFault { get; init; }
    /// <summary>获取失败被客户端确认的 UTC 时间。</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>获取失败所属的 HTTP 指令阶段名称。</summary>
    public required string PhaseName { get; init; }

    /// <summary>获取请求使用的真实电表 meter_id。</summary>
    public required int MeterId { get; init; }

    /// <summary>获取负责接收 TCP 指令的模拟终端设备编号。</summary>
    public required string DeviceId { get; init; }

    /// <summary>获取请求是否要求老 API 同步返回设备业务结果。</summary>
    public required bool ShowResult { get; init; }

    /// <summary>获取当前节点内单调递增的请求序号，用于和日志时间线交叉定位。</summary>
    public required long RequestSequence { get; init; }

    /// <summary>获取 HTTP 状态码；DNS、网络连接或请求超时时为空。</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>获取稳定错误码，例如 API_BUSINESS_407、API_TIMEOUT 或 API_NETWORK_ERROR。</summary>
    public required string ErrorCode { get; init; }

    /// <summary>获取服务端返回或客户端捕获的简短错误消息；无可用消息时为空。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>获取从发起请求到确认失败的耗时，单位毫秒。</summary>
    public required long ElapsedMilliseconds { get; init; }

    /// <summary>获取该失败是否由单请求超时触发。</summary>
    public required bool TimedOut { get; init; }

    /// <summary>获取该失败是否属于 405/407 等明确未进入命令队列的数据预校验拒绝。</summary>
    public required bool PrevalidationRejected { get; init; }

    /// <summary>获取产生该请求的零基负载分片编号。</summary>
    public required int ShardIndex { get; init; }
}
