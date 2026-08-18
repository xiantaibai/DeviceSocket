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
        if (options.Devices.GenerationMode == DeviceAddressGenerationMode.Csv)
            return AllocateCsvDevices(options, devices, runSeed);

        var duplicateCount = (int)Math.Floor(
            options.Devices.Count * options.Devices.DuplicateIdentityPercentage / 100d);
        var uniqueIdentityCount = Math.Max(1, options.Devices.Count - duplicateCount);
        var randomAddressRange = options.Devices.GenerationMode == DeviceAddressGenerationMode.Random
            ? checked(options.Devices.AddressEnd!.Value - options.Devices.AddressStart + 1)
            : 0;
        var randomAddressOffset = randomAddressRange > 0
            ? unchecked((uint)runSeed) % randomAddressRange
            : 0;
        var randomAddressStep = randomAddressRange > 0
            ? FindCoprimeStep(unchecked((uint)CombineSeed(runSeed, uniqueIdentityCount)), randomAddressRange)
            : 0;
        for (var ordinal = options.Distributed.ShardIndex;
             ordinal < options.Devices.Count;
             ordinal += options.Distributed.ShardCount)
        {
            // 重复登录场景只复用协议身份，仍保留唯一 GlobalOrdinal 和随机种子以独立驱动每条 TCP 连接。
            var identityOrdinal = ordinal % uniqueIdentityCount;
            if (options.Target.Protocol == LoadProtocol.QGdw3761)
            {
                var terminalAddress = options.Devices.GenerationMode == DeviceAddressGenerationMode.Random
                    ? checked((ushort)(options.Devices.AddressStart +
                        ((randomAddressOffset + identityOrdinal * randomAddressStep) % randomAddressRange)))
                    : AllocateSequentialQGdwAddress(options.Devices.AddressStart, identityOrdinal);
                var administrativeDivision = options.Devices.GenerationMode == DeviceAddressGenerationMode.Random
                    ? options.Devices.AdministrativeDivision
                    : AllocateSequentialAdministrativeDivision(options.Devices, identityOrdinal);
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
    /// 从共享 CSV 按数据行全局序号分配当前节点设备，保证单机和多机使用完全相同的真实身份集合。
    /// </summary>
    /// <param name="options">已经通过基础校验的完整负载配置。</param>
    /// <param name="devices">接收当前分片设备的预分配集合。</param>
    /// <param name="runSeed">由 RunId 派生的稳定随机种子。</param>
    /// <returns>当前分片负责的真实设备描述。</returns>
    /// <exception cref="ConfigurationValidationException">CSV 数据行数与 <c>devices.count</c> 不一致。</exception>
    private static IReadOnlyList<VirtualDeviceDescriptor> AllocateCsvDevices(
        LoadTestOptions options,
        List<VirtualDeviceDescriptor> devices,
        int runSeed)
    {
        var entries = DeviceIdentityCsvLoader.Load(options.Devices.DeviceMappingFile!);
        if (entries.Count != options.Devices.Count)
        {
            throw new ConfigurationValidationException(
            [
                $"devices.count: 配置为 {options.Devices.Count}，但 devices.deviceMappingFile 包含 {entries.Count} 条真实设备映射；必须完全一致。"
            ]);
        }

        foreach (var entry in entries)
        {
            // 以 CSV 数据行号作为全局序号，所有节点都可独立计算且不会重复领取同一真实设备。
            if (entry.GlobalOrdinal % options.Distributed.ShardCount != options.Distributed.ShardIndex)
                continue;
            devices.Add(new VirtualDeviceDescriptor(
                entry.GlobalOrdinal,
                options.Target.Protocol,
                entry.DeviceId,
                entry.AdministrativeDivision,
                entry.TerminalAddress,
                string.Empty,
                CombineSeed(runSeed, entry.GlobalOrdinal)));
        }

        return devices;
    }

    /// <summary>
    /// 按顺序模式计算376.1终端地址，跨区划后从地址 1 重新开始。
    /// </summary>
    /// <param name="addressStart">第一个行政区划内的起始终端地址。</param>
    /// <param name="identityOrdinal">唯一设备身份的全局序号。</param>
    /// <returns>当前设备在所属行政区划内的16位终端地址。</returns>
    private static ushort AllocateSequentialQGdwAddress(long addressStart, int identityOrdinal)
    {
        // 终端地址零不参与压测；线性地址每跨过 65535 个值便映射到下一个行政区划。
        var linearAddress = addressStart - 1 + identityOrdinal;
        return checked((ushort)(linearAddress % ushort.MaxValue + 1));
    }

    /// <summary>
    /// 按顺序模式计算376.1行政区划；未跨越单区划容量时始终返回配置值。
    /// </summary>
    /// <param name="options">设备编号配置。</param>
    /// <param name="identityOrdinal">唯一设备身份的全局序号。</param>
    /// <returns>四位十进制行政区划码。</returns>
    private static string AllocateSequentialAdministrativeDivision(DeviceOptions options, int identityOrdinal)
    {
        var linearAddress = options.AddressStart - 1 + identityOrdinal;
        var divisionOffset = linearAddress / ushort.MaxValue;
        return (int.Parse(
                options.AdministrativeDivision,
                System.Globalization.CultureInfo.InvariantCulture) + divisionOffset)
            .ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 从随机种子派生与区间容量互质的步长，使全局设备序号映射为无重复的伪随机排列。
    /// </summary>
    /// <param name="seed">由 RunId 和设备规模派生的稳定种子。</param>
    /// <param name="range">随机地址闭区间包含的地址数量。</param>
    /// <returns>区间容量为 1 时返回 0；否则返回小于区间容量且与其互质的正步长。</returns>
    private static long FindCoprimeStep(uint seed, long range)
    {
        if (range == 1)
            return 0;

        var step = 1 + seed % (range - 1);
        while (GreatestCommonDivisor(step, range) != 1)
            step = step + 1 == range ? 1 : step + 1;

        return step;
    }

    /// <summary>
    /// 使用欧几里得算法计算两个非负整数的最大公约数。
    /// </summary>
    /// <param name="left">第一个非负整数。</param>
    /// <param name="right">第二个非负整数。</param>
    /// <returns>两个整数的最大公约数。</returns>
    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return left;
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
