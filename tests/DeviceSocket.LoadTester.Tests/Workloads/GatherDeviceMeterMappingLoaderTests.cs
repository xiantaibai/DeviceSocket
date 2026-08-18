using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Workloads.Gather;

namespace DeviceSocket.LoadTester.Tests.Workloads;

/// <summary>
/// 验证真实 meter_id 与虚拟集中器编号的 CSV 映射和分片筛选规则。
/// </summary>
public sealed class GatherDeviceMeterMappingLoaderTests
{
    /// <summary>
    /// 验证共享 CSV 同时接受九位老设备号与规范 DeviceId，并只返回当前分片映射。
    /// </summary>
    [Fact]
    public void CsvMapping_LoadsOnlyCurrentShard()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gather-map-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "meterId,deviceId\n101,260810001\n102,3761:2608-10002\n");
            var options = TestOptionsFactory.Create();
            options.Devices.Count = 2;
            options.Devices.AdministrativeDivision = "2608";
            options.Devices.AddressStart = 10_001;
            options.CommandWorkload.DeviceMappingFile = path;
            var devices = DeviceAddressAllocator.Allocate(options);

            var mappings = GatherDeviceMeterMappingLoader.Load(options, devices);

            Assert.Equal(2, mappings.Count);
            Assert.Equal(101, mappings[0].MeterId);
            Assert.Equal("3761:2608-10002", mappings[1].Device.DeviceId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 验证顺序 meter_id 按全局设备序号生成，使不同分片不会调用同一电表。
    /// </summary>
    [Fact]
    public void SequentialMeterIds_UseGlobalOrdinal()
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 4;
        options.Distributed.ShardCount = 2;
        options.Distributed.ShardIndex = 1;
        options.CommandWorkload.MeterIdStart = 5_000;
        var devices = DeviceAddressAllocator.Allocate(options);

        var mappings = GatherDeviceMeterMappingLoader.Load(options, devices);

        Assert.Equal([5_001, 5_003], mappings.Select(item => item.MeterId));
    }

    /// <summary>
    /// 验证设备使用 CSV 身份模式时，HTTP 指令默认复用同一文件中的真实 meter_id。
    /// </summary>
    [Fact]
    public void CsvDeviceMode_ReusesDeviceMappingForHttpCommands()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gather-map-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "meterId,deviceId\n701,260810007\n809,330112001\n");
            var options = TestOptionsFactory.Create();
            options.Devices.Count = 2;
            options.Devices.GenerationMode = DeviceAddressGenerationMode.Csv;
            options.Devices.DeviceMappingFile = path;
            var devices = DeviceAddressAllocator.Allocate(options);

            var mappings = GatherDeviceMeterMappingLoader.Load(options, devices);

            Assert.Equal([701, 809], mappings.Select(item => item.MeterId));
            Assert.Equal(
                ["3761:2608-10007", "3761:3301-12001"],
                mappings.Select(item => item.Device.DeviceId));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
