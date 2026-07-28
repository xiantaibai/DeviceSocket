using DeviceSocket.Cluster.Contracts.Devices;

namespace DeviceSocket.Tests.Cluster;

/// <summary>
/// 验证业务服务依赖的设备消息入口保持清晰且一致。
/// </summary>
public sealed class DeviceMessageContractTests
{
    /// <summary>
    /// 普通消息和可靠消息必须都使用 Send 前缀，并保留明确的可靠性语义。
    /// </summary>
    [Fact]
    public void DeviceGrain_ExposesConsistentSendMethodNames()
    {
        var methodNames = typeof(IDeviceGrain)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains(nameof(IDeviceGrain.SendBestEffortMessageAsync), methodNames);
        Assert.Contains(nameof(IDeviceGrain.SendReliableMessageAsync), methodNames);
        Assert.DoesNotContain("SendTransientMessageAsync", methodNames);
        Assert.DoesNotContain("SubmitReliableMessageAsync", methodNames);
    }
}
