using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Tests.Devices;

/// <summary>
/// 验证跨负载节点的设备地址分片不会重复或遗漏。
/// </summary>
public sealed class DeviceAddressAllocatorTests
{
    /// <summary>
    /// 验证三个376.1分片合并后恰好覆盖全部全局设备编号。
    /// </summary>
    [Fact]
    public void QGdwShards_AreDisjointAndComplete()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 100;
        options.Distributed.ShardCount = 3;
        var all = new List<VirtualDeviceDescriptor>();

        for (var shard = 0; shard < 3; shard++)
        {
            options.Distributed.ShardIndex = shard;
            all.AddRange(DeviceAddressAllocator.Allocate(options));
        }

        Assert.Equal(100, all.Count);
        Assert.Equal(100, all.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(0, 100), all.Select(item => item.GlobalOrdinal).Order());
    }

    /// <summary>
    /// 验证顺序模式保持四位行政区划不变，并将后五位终端地址从配置起点依次递增。
    /// </summary>
    [Fact]
    public void QGdwSequentialMode_UsesFixedDivisionAndIncreasingTerminalAddress()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 3;
        options.Devices.AdministrativeDivision = "3712";
        options.Devices.AddressStart = 12_001;

        var devices = DeviceAddressAllocator.Allocate(options);

        Assert.Equal(
            ["3761:3712-12001", "3761:3712-12002", "3761:3712-12003"],
            devices.Select(item => item.DeviceId));
        Assert.All(devices, item => Assert.Equal("3712", item.AdministrativeDivision));
    }

    /// <summary>
    /// 验证随机模式在数据库预置区间内无重复抽取，并可由相同 RunId 跨执行复现。
    /// </summary>
    [Fact]
    public void QGdwRandomMode_IsUniqueBoundedAndReproducible()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 20;
        options.Devices.GenerationMode = DeviceAddressGenerationMode.Random;
        options.Devices.AdministrativeDivision = "3712";
        options.Devices.AddressStart = 10_001;
        options.Devices.AddressEnd = 10_100;
        options.Distributed.RunId = "random-address-test";

        var first = DeviceAddressAllocator.Allocate(options);
        var second = DeviceAddressAllocator.Allocate(options);

        Assert.Equal(first.Select(item => item.DeviceId), second.Select(item => item.DeviceId));
        Assert.Equal(20, first.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(first, item =>
        {
            Assert.Equal("3712", item.AdministrativeDivision);
            Assert.InRange(item.TerminalAddress, (ushort)10_001, (ushort)10_100);
        });
    }

    /// <summary>
    /// 验证随机地址在多分片合并后与单节点生成结果完全一致。
    /// </summary>
    [Fact]
    public void QGdwRandomMode_ShardsMatchSingleNodeAllocation()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 50;
        options.Devices.GenerationMode = DeviceAddressGenerationMode.Random;
        options.Devices.AddressStart = 20_001;
        options.Devices.AddressEnd = 20_100;
        options.Distributed.RunId = "random-shard-test";
        var expected = DeviceAddressAllocator.Allocate(options)
            .OrderBy(item => item.GlobalOrdinal)
            .Select(item => item.DeviceId)
            .ToArray();
        var actual = new List<VirtualDeviceDescriptor>();

        options.Distributed.ShardCount = 3;
        for (var shard = 0; shard < options.Distributed.ShardCount; shard++)
        {
            options.Distributed.ShardIndex = shard;
            actual.AddRange(DeviceAddressAllocator.Allocate(options));
        }

        Assert.Equal(expected, actual.OrderBy(item => item.GlobalOrdinal).Select(item => item.DeviceId));
    }

    /// <summary>
    /// 验证645表地址始终使用十二位十进制格式。
    /// </summary>
    [Fact]
    public void Dlt645Address_UsesTwelveDigits()
    {
        var options = TestOptionsFactory.Create(LoadProtocol.Dlt6452007);

        var device = Assert.Single(DeviceAddressAllocator.Allocate(options).Take(1));

        Assert.Equal("000000000001", device.MeterAddress);
        Assert.Equal("645:000000000001", device.DeviceId);
    }

    /// <summary>
    /// 验证重复登录比例只复用设备身份，不会合并独立虚拟连接的全局序号。
    /// </summary>
    [Fact]
    public void DuplicateIdentityPercentage_CreatesIndependentDuplicateConnections()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 10;
        options.Devices.DuplicateIdentityPercentage = 20;

        var devices = DeviceAddressAllocator.Allocate(options);

        Assert.Equal(10, devices.Select(item => item.GlobalOrdinal).Distinct().Count());
        Assert.Equal(8, devices.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// 验证 8 万设备显式跨行政区划时，边界两侧设备地址连续且身份保持唯一。
    /// </summary>
    [Fact]
    public void QGdwLargeAddressSpace_SpansAdministrativeDivisions()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 80_000;
        options.Devices.AdministrativeDivision = "3301";
        options.Devices.SpanAdministrativeDivisions = true;

        var devices = DeviceAddressAllocator.Allocate(options);

        Assert.Equal(80_000, devices.Count);
        Assert.Equal("3761:3301-65535", devices[65_534].DeviceId);
        Assert.Equal("3761:3302-00001", devices[65_535].DeviceId);
        Assert.Equal(80_000, devices.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).Count());
    }
}
