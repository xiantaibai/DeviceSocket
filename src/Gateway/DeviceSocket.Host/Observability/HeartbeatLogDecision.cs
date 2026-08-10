namespace DeviceSocket.Host.Observability;

/// <summary>
/// 表示上行心跳是否以及因何种规则进入通讯日志。
/// </summary>
public enum HeartbeatLogDecision
{
    /// <summary>没有设备白名单或采样规则命中，跳过日志。</summary>
    Suppressed,

    /// <summary>设备编号位于动态白名单，完整记录。</summary>
    DeviceEnabled,

    /// <summary>设备编号命中确定性采样，完整记录。</summary>
    Sampled
}
