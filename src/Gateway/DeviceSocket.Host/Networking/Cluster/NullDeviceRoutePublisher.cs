using DeviceSocket.Core.Runtime.Connections;

namespace DeviceSocket.Host.Networking.Cluster;

/// <summary>
/// Orleans 被显式关闭时使用的空实现，便于单机协议调试而不改变消息处理代码。
/// </summary>
public sealed class NullDeviceRoutePublisher : IDeviceRoutePublisher
{
    /// <summary>单机模式不发布登录路由。</summary>
    public bool TryPublishConnected(DeviceConnectionSnapshot snapshot) => true;

    /// <summary>单机模式不发布活动时间。</summary>
    public bool TryPublishActivity(DeviceConnectionSnapshot snapshot, DateTimeOffset seenAt) => true;

    /// <summary>单机模式不发布断开路由。</summary>
    public bool TryPublishDisconnected(DeviceConnectionSnapshot snapshot, DateTimeOffset disconnectedAt) => true;
}
