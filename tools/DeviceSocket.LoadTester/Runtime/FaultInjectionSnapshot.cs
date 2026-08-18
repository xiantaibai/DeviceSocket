namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 保存当前阶段可在高频收发路径无锁读取的故障参数快照；主动断线参数由场景编排器单独执行，不进入该快照。
/// </summary>
/// <param name="NetworkLatencyMilliseconds">每次上行发送前增加的时延，单位毫秒。</param>
/// <param name="OutboundPacketLossRate">上行整帧丢弃概率，取值 0～1。</param>
/// <param name="InboundPacketLossRate">下行整帧忽略概率，取值 0～1。</param>
/// <param name="CorruptChecksumRate">上行校验字节破坏概率，取值 0～1。</param>
/// <param name="DuplicateFrameRate">上行帧重复发送概率，取值 0～1。</param>
/// <param name="FragmentSizeBytes">半包写入片段大小，单位字节，零表示不拆分。</param>
/// <param name="FragmentDelayMilliseconds">相邻半包片段之间的等待时间，单位毫秒。</param>
internal sealed record FaultInjectionSnapshot(
    int NetworkLatencyMilliseconds,
    double OutboundPacketLossRate,
    double InboundPacketLossRate,
    double CorruptChecksumRate,
    double DuplicateFrameRate,
    int FragmentSizeBytes,
    int FragmentDelayMilliseconds)
{
    /// <summary>
    /// 获取不施加任何网络故障的共享快照。
    /// </summary>
    public static FaultInjectionSnapshot None { get; } = new(0, 0, 0, 0, 0, 0, 0);
}
