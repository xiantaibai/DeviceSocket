namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 配置 <c>reporting.downstreamDiagnostics</c> 下的下行命令关联、未知协议采样和逐请求追踪输出。
/// </summary>
public sealed class DownstreamDiagnosticsOptions
{
    /// <summary>
    /// 获取或设置 <c>reporting.downstreamDiagnostics.enabled</c>；默认 true，表示 HTTP 指令压测期间建立请求与设备下行帧的关联，
    /// 并输出分类汇总和逐请求追踪；false 仅保留旧版累计指标，不生成诊断文件。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 获取或设置 <c>reporting.downstreamDiagnostics.captureUnknownFrames</c>；默认 true，表示为无法识别的下行报文保留受控样本；
    /// false 仍累计分类数量，但不保存原始报文样本。
    /// </summary>
    public bool CaptureUnknownFrames { get; set; } = true;

    /// <summary>
    /// 获取或设置 <c>reporting.downstreamDiagnostics.maxSamplesPerFingerprint</c>；默认 5，单位为每种报文指纹的样本条数，
    /// 取值 0～100；零表示不保存样本但仍统计分类，避免大规模测试因重复未知报文导致内存和磁盘无界增长。
    /// </summary>
    public int MaxSamplesPerFingerprint { get; set; } = 5;

    /// <summary>
    /// 获取或设置 <c>reporting.downstreamDiagnostics.maxTotalSamples</c>；默认 500，单位为当前节点全部未知报文样本数，
    /// 取值 0～10000；零表示不保存任何样本但仍统计分类，用于给多设备不同地址形成的大量唯一指纹设置全局内存和文件上限。
    /// </summary>
    public int MaxTotalSamples { get; set; } = 500;

    /// <summary>
    /// 获取或设置 <c>reporting.downstreamDiagnostics.rawHexMaxBytes</c>；默认 512，单位字节，取值 0～4096；
    /// 零表示样本不保存原始 Hex，只保存 AFN、FN、控制域、地址和长度等结构化字段。
    /// </summary>
    public int RawHexMaxBytes { get; set; } = 512;

    /// <summary>
    /// 获取或设置 <c>reporting.downstreamDiagnostics.correlationTimeoutSeconds</c>；默认 900，单位秒，必须大于零；
    /// 超过该时间仍未收到下行的期望命令不再参与新报文的设备 FIFO 关联，避免历史请求误关联到后续阶段。
    /// </summary>
    public int CorrelationTimeoutSeconds { get; set; } = 900;
}
