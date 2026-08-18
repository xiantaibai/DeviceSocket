namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 指定设备断线后的重连等待策略。
/// </summary>
public enum ReconnectMode
{
    /// <summary>
    /// 断线后不等待立即重连，适合模拟最激进的服务重启连接风暴。
    /// </summary>
    Immediate,

    /// <summary>
    /// 每次按 <c>reconnect.initialDelayMilliseconds</c> 固定等待，便于生成稳定且可预测的重连压力。
    /// </summary>
    Fixed,

    /// <summary>
    /// 按倍数指数退避并加入随机抖动，达到最大延迟后封顶，适合模拟生产终端的自保护重连行为。
    /// </summary>
    ExponentialJitter
}
