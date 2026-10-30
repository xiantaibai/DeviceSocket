namespace DeviceSocket.LoadTester.Reporting;

/// <summary>记录限量设备侧任务应答样本，服务端 CommandId 未在 TCP 帧携带，不能据此宣称精确任务取消。</summary>
public sealed record GatherV4CommandSample
{
    /// <summary>获取本次下行接收时间。</summary>
    public DateTimeOffset ReceivedAtUtc { get; init; }
    /// <summary>获取写入完成或拒绝时间。</summary>
    public DateTimeOffset RecordedAtUtc { get; init; }
    /// <summary>获取所属分片内的真实模拟设备编号。</summary>
    public required string DeviceId { get; init; }
    /// <summary>获取当前设备连接代次，重连后递增。</summary>
    public long SessionGeneration { get; init; }
    /// <summary>获取下行帧指纹，区分原始报文，不能代替业务任务标识。</summary>
    public string? Fingerprint { get; init; }
    /// <summary>获取原始下行指令摘要，包含设备、AFN 和功能项，供定位回复了什么任务。</summary>
    public string? RequestInstruction { get; init; }
    /// <summary>获取回复状态：f1Sent、faultSent、intentionalDrop、queueRejected、writeFailed 或 cancelled。</summary>
    public required string Outcome { get; init; }
    /// <summary>获取本次实际应答 HEX；拒绝、不回复、取消时为空。</summary>
    public string? ResponseHex { get; init; }
}
