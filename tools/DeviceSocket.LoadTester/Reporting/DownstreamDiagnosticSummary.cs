using DeviceSocket.LoadTester.Protocols;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 汇总本节点命令关联、协议识别、未知格式和重复下行数量。
/// </summary>
public sealed record DownstreamDiagnosticSummary
{
    /// <summary>获取已经注册到命令关联器的 HTTP 请求数。</summary>
    public long RegisteredCommands { get; init; }

    /// <summary>获取至少关联到一帧原始下行的唯一命令数。</summary>
    public long CommandsWithDownstream { get; init; }

    /// <summary>获取关联到当前已支持读表格式的唯一命令数。</summary>
    public long RecognizedCommands { get; init; }

    /// <summary>获取关联到当前不支持协议格式的唯一命令数。</summary>
    public long UnsupportedCommands { get; init; }

    /// <summary>获取成功发送业务响应的唯一命令数。</summary>
    public long RespondedCommands { get; init; }

    /// <summary>获取无法关联到本轮请求的下行帧数。</summary>
    public long UnmatchedDownstreamFrames { get; init; }

    /// <summary>获取依据报文指纹识别出的重复或重试下行帧数。</summary>
    public long DuplicateDownstreamFrames { get; init; }

    /// <summary>获取按分类累计的全部下行帧数量，包含重复和无法关联帧。</summary>
    public IReadOnlyDictionary<DownstreamClassificationKind, long> Classifications { get; init; } =
        new Dictionary<DownstreamClassificationKind, long>();
}
