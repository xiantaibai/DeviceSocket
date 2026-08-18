namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示设备异常离线后的重连次数、退避上限和随机化规则。
/// </summary>
public sealed class ReconnectOptions
{
    /// <summary>
    /// 获取或设置 <c>reconnect.mode</c>；默认 <see cref="ReconnectMode.ExponentialJitter"/>，决定连接失败或掉线后的等待算法。
    /// </summary>
    public ReconnectMode Mode { get; set; } = ReconnectMode.ExponentialJitter;

    /// <summary>
    /// 获取或设置 <c>reconnect.initialDelayMilliseconds</c>；单位毫秒，默认 500，固定模式直接使用，指数模式作为首次退避基数，零表示不等待。
    /// </summary>
    public int InitialDelayMilliseconds { get; set; } = 500;

    /// <summary>
    /// 获取或设置 <c>reconnect.maximumDelaySeconds</c>；单位秒，默认 30，限制指数退避的最长等待，零仅适用于立即重连模式。
    /// </summary>
    public int MaximumDelaySeconds { get; set; } = 30;

    /// <summary>
    /// 获取或设置 <c>reconnect.multiplier</c>；默认 2，表示指数退避每失败一次的增长倍数，必须不小于 1。
    /// </summary>
    public double Multiplier { get; set; } = 2;

    /// <summary>
    /// 获取或设置 <c>reconnect.jitterRatio</c>；取值 0～1，默认 0.2，表示在退避值两侧加入的随机比例，零表示所有设备按相同时刻重试。
    /// </summary>
    public double JitterRatio { get; set; } = 0.2;

    /// <summary>
    /// 获取或设置 <c>reconnect.maximumAttempts</c>；单位为连续重连次数，默认 0 表示不限制，达到正数上限后设备停止自动重连。
    /// </summary>
    public int MaximumAttempts { get; set; }
}
