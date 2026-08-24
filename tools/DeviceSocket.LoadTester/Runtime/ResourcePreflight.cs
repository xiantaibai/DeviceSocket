using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 在发起负载前检查进程位数、内存预算、源端口风险和目标解析，帮助区分客户端容量问题与被测服务问题。
/// </summary>
internal static class ResourcePreflight
{
    /// <summary>
    /// 采集本机资源事实并预解析当前角色实际访问的 TCP 或 HTTP 目标地址。
    /// </summary>
    /// <param name="options">完整负载配置。</param>
    /// <param name="localDeviceCount">当前分片设备数量。</param>
    /// <param name="cancellationToken">取消 DNS 解析的令牌。</param>
    /// <returns>运行事实和不阻止测试的风险警告。</returns>
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

        if (options.Role != LoadTestRole.ApiOnly && !Environment.Is64BitProcess && localDeviceCount > 10_000)
            warnings.Add("当前为 32 位进程，大规模连接可能提前耗尽地址空间。");
        if (options.Role != LoadTestRole.ApiOnly)
        {
            TryAppendTcpPortFacts(facts, warnings, localDeviceCount);
            if (localDeviceCount >= 8_000 && options.Scenario.Phases.Any(item => item.ExpectedServerRestart))
            {
                warnings.Add(
                    "本分片将在服务端重启后集中重连；单一源 IP 的临时端口和 TIME_WAIT 可能先于服务端耗尽，" +
                    "建议降低单机分片规模、增加负载机/源 IP，且不要把 SOCKET_AddressAlreadyInUse 直接归因于服务端。");
            }
            else if (localDeviceCount > 15_000)
            {
                warnings.Add("单一源 IP 连接固定目标端口可能受临时端口和 TIME_WAIT 限制，建议使用多源 IP 或多台负载机。");
            }
        }
        var availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (availableMemory > 0)
            facts.Add($"运行时可用内存估计: {availableMemory / 1024d / 1024d:N0} MiB");
        if (options.Role != LoadTestRole.ApiOnly && availableMemory > 0 && availableMemory < localDeviceCount * 32_768L)
            warnings.Add("可用内存低于每设备 32 KiB 的保守预算，负载机可能先于服务端成为瓶颈。");

        try
        {
            var targetHost = options.Role == LoadTestRole.ApiOnly
                ? new Uri(options.CommandWorkload.BaseUrl, UriKind.Absolute).Host
                : options.Target.Host;
            var addresses = await Dns.GetHostAddressesAsync(targetHost, cancellationToken);
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

    /// <summary>
    /// 尝试读取操作系统 TCP 连接表并记录已建立连接和 TIME_WAIT 数量；读取失败只产生警告，不阻止压测。
    /// </summary>
    /// <param name="facts">追加可验证运行事实的集合。</param>
    /// <param name="warnings">追加非阻断风险提示的集合。</param>
    /// <param name="localDeviceCount">当前分片计划模拟的设备数。</param>
    private static void TryAppendTcpPortFacts(
        ICollection<string> facts,
        ICollection<string> warnings,
        int localDeviceCount)
    {
        try
        {
            var connections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            var established = connections.LongCount(item => item.State == TcpState.Established);
            var timeWait = connections.LongCount(item => item.State == TcpState.TimeWait);
            facts.Add($"运行前本机 TCP: Established {established:N0}，TIME_WAIT {timeWait:N0}");
            if (timeWait >= Math.Max(2_000, localDeviceCount / 2))
            {
                warnings.Add(
                    $"运行前已存在 {timeWait:N0} 条 TIME_WAIT，接近本分片 {localDeviceCount:N0} 台设备的端口预算，" +
                    "建议等待回收或改用空闲负载机后再开始正式测试。");
            }
        }
        catch (NetworkInformationException exception)
        {
            warnings.Add($"无法读取本机 TCP 连接表，未能预判临时端口风险：{exception.Message}");
        }
    }
}
