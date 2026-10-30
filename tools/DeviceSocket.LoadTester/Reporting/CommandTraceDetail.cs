using System.Text.Json.Serialization;
using DeviceSocket.LoadTester.Protocols;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示一条 HTTP 设备指令从计划、API 返回、设备下行到模拟应答的完整诊断记录。
/// </summary>
public sealed record CommandTraceDetail
{
    /// <summary>获取节点内唯一请求标识。</summary>
    public required long RequestId { get; init; }

    /// <summary>获取所属 HTTP 阶段名称。</summary>
    public required string PhaseName { get; init; }

    /// <summary>获取报告中稳定的请求序号。</summary>
    public required long RequestSequence { get; init; }

    /// <summary>获取真实数据库电表编号。</summary>
    public required int MeterId { get; init; }

    /// <summary>获取模拟集中器设备编号。</summary>
    public required string DeviceId { get; init; }

    /// <summary>获取请求是否同步等待业务结果。</summary>
    public required bool ShowResult { get; init; }

    /// <summary>获取请求注册到关联器的 UTC 时间。</summary>
    public required DateTimeOffset RegisteredAtUtc { get; init; }

    /// <summary>获取 HTTP 完成时间；尚未完成时为空。</summary>
    public DateTimeOffset? HttpCompletedAtUtc { get; init; }

    /// <summary>获取 HTTP 状态码；网络错误或超时时为空。</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>获取 HTTP 状态及业务 JSON 是否成功。</summary>
    public bool? ApiSucceeded { get; init; }

    /// <summary>获取稳定 API 错误码；成功时为空。</summary>
    public string? ApiErrorCode { get; init; }

    /// <summary>获取是否属于明确未入队的预校验拒绝。</summary>
    public bool PrevalidationRejected { get; init; }

    /// <summary>获取关联到首个原始下行帧的 UTC 时间。</summary>
    public DateTimeOffset? FirstDownstreamAtUtc { get; init; }

    /// <summary>获取首个关联下行帧的分类。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DownstreamClassificationKind? DownstreamKind { get; init; }

    /// <summary>获取下行关联可信度。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CommandAssociationConfidence AssociationConfidence { get; init; }

    /// <summary>获取首个下行帧的稳定指纹。</summary>
    public string? DownstreamFingerprint { get; init; }

    /// <summary>获取同一指纹后续重复或重试下行次数。</summary>
    public int DuplicateDownstreamFrames { get; init; }

    /// <summary>获取模拟设备首次成功写出业务响应的 UTC 时间。</summary>
    public DateTimeOffset? ResponseSentAtUtc { get; init; }

    /// <summary>获取模拟设备是否成功写出至少一条业务响应。</summary>
    public bool ResponseSent { get; init; }

    /// <summary>获取一次 HTTP 请求预期生成的下行命令数；来自预检配置，默认 1。</summary>
    public int ExpectedDownstreamCommands { get; init; } = 1;
    /// <summary>获取按设备 FIFO 推断关联的不同下行命令数；不能冒充 CommandId 精确关联。</summary>
    public int ObservedDownstreamCommands { get; init; }
    /// <summary>获取成功写出正确关联应答的子命令数；错误 A3/PSEQ 和故意不回复不计入。</summary>
    public int CorrectlyRespondedCommands { get; init; }

    /// <summary>获取命令最终诊断结果。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required CommandTraceOutcome Outcome { get; init; }
}
