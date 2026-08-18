using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 以原子快照方式发布当前场景阶段的故障参数，使一万条设备会话切换阶段时无需逐连接加锁。
/// </summary>
internal sealed class FaultInjectionController
{
    private FaultInjectionSnapshot _current = FaultInjectionSnapshot.None;

    internal FaultInjectionSnapshot Current => Volatile.Read(ref _current);

    internal void Apply(FaultInjectionOptions options)
    {
        var snapshot = new FaultInjectionSnapshot(
            options.NetworkLatencyMilliseconds,
            options.OutboundPacketLossRate,
            options.InboundPacketLossRate,
            options.CorruptChecksumRate,
            options.DuplicateFrameRate,
            options.FragmentSizeBytes,
            options.FragmentDelayMilliseconds);
        Volatile.Write(ref _current, snapshot);
    }

    internal void Reset() => Volatile.Write(ref _current, FaultInjectionSnapshot.None);

    internal static bool ShouldInject(double rate, Random random) =>
        rate >= 1 || (rate > 0 && random.NextDouble() < rate);
}
