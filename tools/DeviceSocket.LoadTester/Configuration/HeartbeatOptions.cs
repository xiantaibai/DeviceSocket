namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示设备在线后的心跳频率、随机抖动和确认超时。
/// </summary>
public sealed class HeartbeatOptions
{
    /// <summary>
    /// 获取或设置 <c>heartbeat.enabled</c>；默认 true，设备登录后按协议周期发送心跳；false 用于仅测试长连接空闲保持能力。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 获取或设置 <c>heartbeat.intervalSeconds</c>；单位秒，默认 60，启用心跳时必须大于零。
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// 获取或设置 <c>heartbeat.jitterSeconds</c>；单位秒，默认 5，用于按设备打散心跳，必须小于心跳周期，零表示不抖动。
    /// </summary>
    public int JitterSeconds { get; set; } = 5;

    /// <summary>
    /// 获取或设置 <c>heartbeat.responseTimeoutSeconds</c>；单位秒，默认 5，限制等待心跳确认的时间，零值无效；超时会进入断线重连流程。
    /// </summary>
    public int ResponseTimeoutSeconds { get; set; } = 5;
}
