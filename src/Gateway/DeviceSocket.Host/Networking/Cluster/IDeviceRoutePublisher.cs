using DeviceSocket.Core.Runtime.Connections;

namespace DeviceSocket.Host.Networking.Cluster;

/// <summary>
/// 把本节点连接变化异步发布到设备 Orleans Grain，Socket 收包线程不等待远程集群。
/// </summary>
public interface IDeviceRoutePublisher
{
    /// <summary>尝试发布设备登录事件；队列已满或正在停止时返回 false。</summary>
    bool TryPublishConnected(DeviceConnectionSnapshot snapshot);

    /// <summary>尝试发布设备活动事件；队列已满或正在停止时返回 false。</summary>
    bool TryPublishActivity(DeviceConnectionSnapshot snapshot, DateTimeOffset seenAt);

    /// <summary>尝试发布设备断开事件；队列已满或正在停止时返回 false。</summary>
    bool TryPublishDisconnected(DeviceConnectionSnapshot snapshot, DateTimeOffset disconnectedAt);
}
