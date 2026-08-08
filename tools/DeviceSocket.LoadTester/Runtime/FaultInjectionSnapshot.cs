namespace DeviceSocket.LoadTester.Runtime;

internal sealed record FaultInjectionSnapshot(
    int NetworkLatencyMilliseconds,
    double OutboundPacketLossRate,
    double InboundPacketLossRate,
    double CorruptChecksumRate,
    double DuplicateFrameRate,
    int FragmentSizeBytes,
    int FragmentDelayMilliseconds)
{
    public static FaultInjectionSnapshot None { get; } = new(0, 0, 0, 0, 0, 0, 0);
}
