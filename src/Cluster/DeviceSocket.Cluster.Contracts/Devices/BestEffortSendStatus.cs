namespace DeviceSocket.Cluster.Contracts.Devices;

/// <summary>
/// 尽力发送普通消息的处理结果，失败状态不会进入持久化重试。
/// </summary>
public enum BestEffortSendStatus
{
    /// <summary>报文已经写入设备 Socket。</summary>
    WrittenToSocket = 0,

    /// <summary>设备离线或路由已经过期，消息已丢弃。</summary>
    DroppedDeviceOffline = 1,

    /// <summary>设备所在网关不可用，消息已丢弃。</summary>
    DroppedGatewayUnavailable = 2,

    /// <summary>协议、报文或调用参数不合法。</summary>
    Rejected = 3,

    /// <summary>调用结果不确定，普通消息不会自动重试。</summary>
    DeliveryUncertain = 4
}
