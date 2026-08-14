using DeviceSocket.Cluster.Contracts.Devices;

namespace DeviceSocket.Host.Networking.Cluster;

/// <summary>
/// Orleans 被关闭时使用的设备命令回复空发布器；保留关联以便调试环境能够观察重复或迟到响应。
/// </summary>
public sealed class NullDeviceCommandReplyPublisher : IDeviceCommandReplyPublisher
{
    /// <inheritdoc />
    public Task<DeviceCommandReplyPublishResult> PublishAsync(DeviceCommandReply reply) =>
        Task.FromResult(new DeviceCommandReplyPublishResult
        {
            Outcome = DeviceCommandReplyPublishOutcome.Unavailable,
            Code = "ORLEANS_DISABLED"
        });
}
