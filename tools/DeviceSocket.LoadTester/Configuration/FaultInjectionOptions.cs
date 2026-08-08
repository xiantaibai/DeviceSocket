namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示场景阶段内由客户端主动施加的断线、时延、丢包、半包、粘包和校验和故障。
/// </summary>
public sealed class FaultInjectionOptions
{
    public int NetworkLatencyMilliseconds { get; set; }
    public double OutboundPacketLossRate { get; set; }
    public double InboundPacketLossRate { get; set; }
    public double CorruptChecksumRate { get; set; }
    public double DuplicateFrameRate { get; set; }
    public int FragmentSizeBytes { get; set; }
    public int FragmentDelayMilliseconds { get; set; }
    public double DisconnectPercentage { get; set; }
    public int DisconnectBatchSize { get; set; }
    public int DisconnectIntervalMilliseconds { get; set; }
}
