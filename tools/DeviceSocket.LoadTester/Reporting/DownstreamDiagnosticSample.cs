using System.Text.Json.Serialization;
using DeviceSocket.LoadTester.Protocols;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 表示按报文指纹限量保存的一条未知或不受支持下行样本。
/// </summary>
public sealed record DownstreamDiagnosticSample
{
    /// <summary>获取收到报文的 UTC 时间。</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>获取可能关联的 HTTP 阶段；无法关联时为空。</summary>
    public string? PhaseName { get; init; }

    /// <summary>获取可能关联的请求序号；无法关联时为空。</summary>
    public long? RequestSequence { get; init; }

    /// <summary>获取可能关联的电表编号；无法关联时为空。</summary>
    public int? MeterId { get; init; }

    /// <summary>获取收到报文的模拟设备编号。</summary>
    public required string DeviceId { get; init; }

    /// <summary>获取报文分类。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required DownstreamClassificationKind Kind { get; init; }

    /// <summary>获取地址匹配状态；无法判断时为空。</summary>
    public bool? AddressMatched { get; init; }

    /// <summary>获取可空控制域十六进制文本。</summary>
    public string? ControlHex { get; init; }

    /// <summary>获取可空 AFN 十六进制文本。</summary>
    public string? AfnHex { get; init; }

    /// <summary>获取 PN 集合。</summary>
    public IReadOnlyList<int> Points { get; init; } = [];

    /// <summary>获取 FN 集合。</summary>
    public IReadOnlyList<int> Functions { get; init; } = [];

    /// <summary>获取可空 PSEQ。</summary>
    public byte? Sequence { get; init; }

    /// <summary>获取完整原始报文长度，单位字节。</summary>
    public required int RawLength { get; init; }

    /// <summary>获取按配置截断后的原始 Hex；配置为零时为空。</summary>
    public string? RawHex { get; init; }

    /// <summary>获取报文稳定指纹。</summary>
    public required string Fingerprint { get; init; }

    /// <summary>获取中文分类原因。</summary>
    public required string Reason { get; init; }

    /// <summary>获取当前样本是否被判断为重复或重试帧。</summary>
    public bool Duplicate { get; init; }
}
