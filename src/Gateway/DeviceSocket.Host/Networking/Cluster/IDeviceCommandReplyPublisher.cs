using DeviceSocket.Cluster.Contracts.Devices;

namespace DeviceSocket.Host.Networking.Cluster;

/// <summary>
/// 把 Gateway 已完成协议关联的设备回复提交给对应 DeviceGrain。
/// </summary>
public interface IDeviceCommandReplyPublisher
{
    /// <summary>
    /// 发布设备命令回复；网络异常可以抛出，由协议 Handler 释放关联发布权后等待重试。
    /// </summary>
    /// <param name="reply">包含原命令 MsgId、AttemptNo 和 SessionEpoch 的协议回复。</param>
    /// <returns>Orleans 是否应用回复以及 Gateway 应如何处理关联。</returns>
    Task<DeviceCommandReplyPublishResult> PublishAsync(DeviceCommandReply reply);
}
