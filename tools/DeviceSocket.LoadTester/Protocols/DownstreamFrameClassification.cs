namespace DeviceSocket.LoadTester.Protocols;

/// <summary>
/// 保存一条下行帧的结构化分类，供命令关联、未知样本聚合和报告解释共同使用。
/// </summary>
internal sealed record DownstreamFrameClassification
{
    /// <summary>获取稳定分类枚举。</summary>
    public required DownstreamClassificationKind Kind { get; init; }

    /// <summary>获取是否属于当前模拟器已经支持并可生成业务应答的读表命令。</summary>
    public required bool IsRecognizedCommand { get; init; }

    /// <summary>获取是否为有效但当前业务模拟器不支持的命令格式。</summary>
    public required bool IsUnsupportedCommand { get; init; }

    /// <summary>获取报文设备地址是否与当前会话一致；无法解析地址时为空。</summary>
    public bool? AddressMatched { get; init; }

    /// <summary>获取可空 376.1 控制域。</summary>
    public byte? Control { get; init; }

    /// <summary>获取可空 AFN（应用层功能码）。</summary>
    public byte? Afn { get; init; }

    /// <summary>获取报文携带的 PN（测量点）集合。</summary>
    public IReadOnlyList<int> Points { get; init; } = [];

    /// <summary>获取报文携带的 FN（功能）集合。</summary>
    public IReadOnlyList<int> Functions { get; init; } = [];

    /// <summary>获取可空 PSEQ（启动帧序号）。</summary>
    public byte? Sequence { get; init; }

    /// <summary>获取报文原始长度，单位字节。</summary>
    public required int RawLength { get; init; }

    /// <summary>获取用于识别重试和限制未知样本数量的稳定指纹。</summary>
    public required string Fingerprint { get; init; }

    /// <summary>获取原始报文字节；调用方必须按配置截断后才能写入报告。</summary>
    public required byte[] Raw { get; init; }

    /// <summary>获取面向排障人员的简短分类原因。</summary>
    public required string Reason { get; init; }
}
