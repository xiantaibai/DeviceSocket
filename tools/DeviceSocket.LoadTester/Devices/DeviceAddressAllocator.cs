using System.Text;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Devices;

/// <summary>
/// 按全局序号和分片编号分配协议地址，保证多负载机生成的设备身份互不重叠。
/// </summary>
public static class DeviceAddressAllocator
{
    /// <summary>
    /// 为当前分片生成设备描述；全局序号采用 shardIndex + n * shardCount 规则。
    /// </summary>
    /// <param name="options">已经通过校验的完整负载配置。</param>
    /// <returns>只属于当前分片且协议身份互不冲突的虚拟设备描述。</returns>
    public static IReadOnlyList<VirtualDeviceDescriptor> Allocate(LoadTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        LoadTestOptionsValidator.ValidateAndThrow(options);

        var devices = new List<VirtualDeviceDescriptor>(
            (options.Devices.Count + options.Distributed.ShardCount - 1) / options.Distributed.ShardCount);
        var runSeed = StableHash(options.Distributed.RunId);
        var duplicateCount = (int)Math.Floor(
            options.Devices.Count * options.Devices.DuplicateIdentityPercentage / 100d);
        var uniqueIdentityCount = Math.Max(1, options.Devices.Count - duplicateCount);
        for (var ordinal = options.Distributed.ShardIndex;
             ordinal < options.Devices.Count;
             ordinal += options.Distributed.ShardCount)
        {
            // 重复登录场景只复用协议身份，仍保留唯一 GlobalOrdinal 和随机种子以独立驱动每条 TCP 连接。
            var identityOrdinal = ordinal % uniqueIdentityCount;
            if (options.Target.Protocol == LoadProtocol.QGdw3761)
            {
                // 终端地址零不参与压测；超过 65535 时按显式配置递增四位行政区划并从 1 继续。
                var linearAddress = options.Devices.AddressStart - 1 + identityOrdinal;
                var divisionOffset = linearAddress / ushort.MaxValue;
                var administrativeDivision = (int.Parse(
                    options.Devices.AdministrativeDivision,
                    System.Globalization.CultureInfo.InvariantCulture) + divisionOffset)
                    .ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                var terminalAddress = checked((ushort)(linearAddress % ushort.MaxValue + 1));
                devices.Add(new VirtualDeviceDescriptor(
                    ordinal,
                    options.Target.Protocol,
                    $"3761:{administrativeDivision}-{terminalAddress:D5}",
                    administrativeDivision,
                    terminalAddress,
                    string.Empty,
                    CombineSeed(runSeed, ordinal)));
            }
            else
            {
                var address = options.Devices.AddressStart + identityOrdinal;
                var meterAddress = address.ToString("D12", System.Globalization.CultureInfo.InvariantCulture);
                devices.Add(new VirtualDeviceDescriptor(
                    ordinal,
                    options.Target.Protocol,
                    $"645:{meterAddress}",
                    string.Empty,
                    0,
                    meterAddress,
                    CombineSeed(runSeed, ordinal)));
            }
        }

        return devices;
    }

    /// <summary>
    /// 使用 FNV-1a 为 RunId 生成跨进程稳定的随机种子。
    /// </summary>
    /// <param name="value">本轮测试 RunId。</param>
    /// <returns>跨负载节点一致的 32 位种子。</returns>
    private static int StableHash(string value)
    {
        // string.GetHashCode 在不同进程会随机化；FNV-1a 保证相同 RunId 在多台负载机上得到相同种子。
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var item in Encoding.UTF8.GetBytes(value))
        {
            hash ^= item;
            hash *= prime;
        }

        return unchecked((int)hash);
    }

    /// <summary>
    /// 把运行种子与全局设备序号组合为单连接随机种子。
    /// </summary>
    /// <param name="runSeed">本轮稳定运行种子。</param>
    /// <param name="ordinal">虚拟连接全局序号。</param>
    /// <returns>当前虚拟连接的确定性随机种子。</returns>
    private static int CombineSeed(int runSeed, int ordinal) =>
        unchecked((runSeed * 397) ^ ordinal);
}
