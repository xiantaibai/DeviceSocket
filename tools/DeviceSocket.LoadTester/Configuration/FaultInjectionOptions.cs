namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示场景阶段内由客户端主动施加的断线、时延、丢包、半包、粘包和校验和故障。
/// </summary>
public sealed class FaultInjectionOptions
{
    /// <summary>
    /// 获取或设置 <c>scenario.phases[].faults.networkLatencyMilliseconds</c>；单位毫秒，默认 0 表示不主动增加每次上行发送前的网络时延。
    /// </summary>
    public int NetworkLatencyMilliseconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].faults.outboundPacketLossRate</c>；取值 0～1，默认 0 表示不丢弃终端上行帧。
    /// </summary>
    public double OutboundPacketLossRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].faults.inboundPacketLossRate</c>；取值 0～1，默认 0 表示不丢弃服务端下行帧，用于模拟终端未收到命令。
    /// </summary>
    public double InboundPacketLossRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].faults.corruptChecksumRate</c>；取值 0～1，默认 0，命中时破坏上行帧校验字节以验证服务端非法帧处理。
    /// </summary>
    public double CorruptChecksumRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].faults.duplicateFrameRate</c>；取值 0～1，默认 0，命中时重复发送同一上行帧以验证幂等和粘包路径。
    /// </summary>
    public double DuplicateFrameRate { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].faults.fragmentSizeBytes</c>；单位字节，默认 0 表示完整写入，大于 0 时按该尺寸拆分帧以制造 TCP 半包。
    /// </summary>
    public int FragmentSizeBytes { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].faults.fragmentDelayMilliseconds</c>；单位毫秒，默认 0，控制半包片段之间的等待时间。
    /// </summary>
    public int FragmentDelayMilliseconds { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].faults.disconnectPercentage</c>；单位百分比，默认 0，表示阶段开始时主动断开的目标设备比例，100 表示全部设备。
    /// </summary>
    public double DisconnectPercentage { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].faults.disconnectBatchSize</c>；单位设备数，默认 0 表示一次性断开全部目标，大于 0 时按批次制造断线风暴。
    /// </summary>
    public int DisconnectBatchSize { get; set; }

    /// <summary>
    /// 获取或设置 <c>scenario.phases[].faults.disconnectIntervalMilliseconds</c>；单位毫秒，默认 0，表示相邻主动断线批次之间不等待。
    /// </summary>
    public int DisconnectIntervalMilliseconds { get; set; }
}
