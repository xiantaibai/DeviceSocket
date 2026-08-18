namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 配置 <c>workload.gatherReadingResponse</c> 下 Gather 老服务读表命令的终端应答模型。
/// </summary>
public sealed class GatherReadingResponseOptions
{
    /// <summary>
    /// 获取或设置 <c>workload.gatherReadingResponse.enabled</c>；默认 true，表示识别 AFN=0CH/F129 后返回业务帧，false 会保留通用下行统计但不应答读表命令。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 获取或设置 <c>workload.gatherReadingResponse.mode</c>；默认 normal，仅对命中 <see cref="AffectedDevicePercentage"/> 的设备应用非正常模式。
    /// </summary>
    public GatherReadingResponseMode Mode { get; set; } = GatherReadingResponseMode.Normal;

    /// <summary>
    /// 获取或设置 <c>workload.gatherReadingResponse.affectedDevicePercentage</c>；单位为百分比，默认 100，零表示非正常模式不影响任何设备。
    /// </summary>
    public double AffectedDevicePercentage { get; set; } = 100;

    /// <summary>
    /// 获取或设置 <c>workload.gatherReadingResponse.delayMilliseconds</c>；单位毫秒，默认 1000，仅 delayed 模式使用，零表示不额外等待。
    /// </summary>
    public int DelayMilliseconds { get; set; } = 1_000;

    /// <summary>
    /// 获取或设置 <c>workload.gatherReadingResponse.rateCount</c>；默认 4，表示读表业务帧携带的费率示值数量，允许 0～8。
    /// </summary>
    public int RateCount { get; set; } = 4;

    /// <summary>
    /// 获取或设置 <c>workload.gatherReadingResponse.baseReadingKwh</c>；单位 kWh，默认 1000，作为首台虚拟设备的总电能示值。
    /// </summary>
    public decimal BaseReadingKwh { get; set; } = 1_000m;

    /// <summary>
    /// 获取或设置 <c>workload.gatherReadingResponse.deviceStepKwh</c>；单位 kWh，默认 0.01，每增加一个全局设备序号便增加该值以避免所有终端数据完全相同。
    /// </summary>
    public decimal DeviceStepKwh { get; set; } = 0.01m;
}
