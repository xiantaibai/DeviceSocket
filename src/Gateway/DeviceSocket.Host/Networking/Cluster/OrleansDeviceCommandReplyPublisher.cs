using DeviceSocket.Cluster.Contracts.Commands;
using DeviceSocket.Cluster.Contracts.Devices;
using Orleans;

namespace DeviceSocket.Host.Networking.Cluster;

/// <summary>
/// 使用 Orleans Client 将已关联的设备协议回复提交给设备单线程状态机。
/// </summary>
public sealed class OrleansDeviceCommandReplyPublisher(IClusterClient clusterClient)
    : IDeviceCommandReplyPublisher
{
    /// <inheritdoc />
    public async Task<DeviceCommandReplyPublishResult> PublishAsync(DeviceCommandReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var receipt = await clusterClient
            .GetGrain<IDeviceGrain>(reply.DeviceId)
            .ReceiveCommandReplyAsync(reply);

        return new DeviceCommandReplyPublishResult
        {
            Outcome = ResolveOutcome(receipt),
            Code = receipt.Code
        };
    }

    /// <summary>
    /// 把 Orleans 命令状态折叠为 Gateway 关联处理动作。
    /// </summary>
    /// <param name="receipt">DeviceGrain 返回的可靠命令回执。</param>
    /// <returns>删除、挂起、释放或拒绝关联的动作。</returns>
    private static DeviceCommandReplyPublishOutcome ResolveOutcome(ReliableMessageReceipt receipt)
    {
        if (!receipt.Accepted)
            return DeviceCommandReplyPublishOutcome.Rejected;
        if (receipt.Status == ReliableMessageStatus.RetryPending)
            return DeviceCommandReplyPublishOutcome.RetryScheduled;
        if (receipt.Status is ReliableMessageStatus.Confirmed or
            ReliableMessageStatus.CompletedWithoutConfirmation or
            ReliableMessageStatus.Expired or
            ReliableMessageStatus.Failed or
            ReliableMessageStatus.Cancelled)
        {
            return DeviceCommandReplyPublishOutcome.Completed;
        }

        return DeviceCommandReplyPublishOutcome.NotApplied;
    }
}
