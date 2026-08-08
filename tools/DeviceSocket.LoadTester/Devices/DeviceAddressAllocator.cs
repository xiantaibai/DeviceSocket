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
            var address = options.Devices.AddressStart + identityOrdinal;
            if (options.Target.Protocol == LoadProtocol.QGdw3761)
            {
                var terminalAddress = checked((ushort)address);
                devices.Add(new VirtualDeviceDescriptor(
                    ordinal,
                    options.Target.Protocol,
                    $"3761:{options.Devices.AdministrativeDivision}-{terminalAddress:D5}",
                    options.Devices.AdministrativeDivision,
                    terminalAddress,
                    string.Empty,
                    CombineSeed(runSeed, ordinal)));
            }
            else
            {
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

    private static int CombineSeed(int runSeed, int ordinal) =>
        unchecked((runSeed * 397) ^ ordinal);
}
