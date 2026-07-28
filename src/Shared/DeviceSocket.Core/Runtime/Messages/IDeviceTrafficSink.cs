namespace DeviceSocket.Core.Runtime.Messages;

/// <summary>
/// 设备原始报文审计日志入口，调用方只负责非阻塞提交。
/// </summary>
public interface IDeviceTrafficSink
{
    /// <summary>
    /// 尝试提交一条通信记录；同步提交失败时返回 false，不能阻塞 Socket 线程。
    /// 底层异步 Sink 后续丢弃由独立健康监控记录。
    /// </summary>
    bool TryWrite(DeviceTrafficRecord record);
}
