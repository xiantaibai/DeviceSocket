using System.Net;
using System.Net.Sockets;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Runtime;

internal static class ResourcePreflight
{
    internal static async Task<ResourcePreflightResult> CheckAsync(
        LoadTestOptions options,
        int localDeviceCount,
        CancellationToken cancellationToken)
    {
        var facts = new List<string>
        {
            $"运行时: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}",
            $"操作系统: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
            $"CPU: {Environment.ProcessorCount} 核",
            $"进程位数: {(Environment.Is64BitProcess ? 64 : 32)} 位",
            $"本分片设备数: {localDeviceCount:N0}"
        };
        var warnings = new List<string>();

        if (!Environment.Is64BitProcess && localDeviceCount > 10_000)
            warnings.Add("当前为 32 位进程，大规模连接可能提前耗尽地址空间。");
        if (localDeviceCount > 15_000)
            warnings.Add("单一源 IP 连接固定目标端口可能受临时端口和 TIME_WAIT 限制，建议使用多源 IP 或多台负载机。");
        var availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (availableMemory > 0)
            facts.Add($"运行时可用内存估计: {availableMemory / 1024d / 1024d:N0} MiB");
        if (availableMemory > 0 && availableMemory < localDeviceCount * 32_768L)
            warnings.Add("可用内存低于每设备 32 KiB 的保守预算，负载机可能先于服务端成为瓶颈。");

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(options.Target.Host, cancellationToken);
            facts.Add($"目标解析: {string.Join(", ", addresses.Select(item => item.ToString()))}");
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            warnings.Add($"目标主机预解析失败：{exception.Message}");
        }

        if (options.Distributed.StartAtUtc is { } startAt && startAt < DateTimeOffset.UtcNow.AddSeconds(-1))
            warnings.Add("计划启动时间已经过去，当前节点将立即启动，可能无法与其他分片同步。");

        return new ResourcePreflightResult(facts, warnings);
    }
}
