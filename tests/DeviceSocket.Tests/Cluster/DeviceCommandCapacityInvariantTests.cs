using DeviceSocket.Cluster.Grains.Commands;
using DeviceSocket.Cluster.Grains.Devices;
using DeviceSocket.Cluster.Grains.Scheduling;
using Orleans.Runtime;

namespace DeviceSocket.Tests.Cluster;

/// <summary>
/// 验证百万设备设计中不能依赖运行环境才发现的固定分片、稳定哈希和 Timer/Reminder 数量约束。
/// </summary>
public sealed class DeviceCommandCapacityInvariantTests
{
    /// <summary>
    /// 验证一百万个稳定设备编号能覆盖全部 1024 分片且负载没有明显热点。
    /// </summary>
    [Fact]
    public void ShardResolver_DistributesOneMillionDevicesAcrossFixedShards()
    {
        const int deviceCount = 1_000_000;
        const int shardCount = 1024;
        var counts = new int[shardCount];

        for (var index = 0; index < deviceCount; index++)
        {
            var shard = DeviceCommandShardResolver.Resolve($"device-{index:D7}", shardCount);
            counts[shard]++;
        }

        Assert.DoesNotContain(0, counts);
        Assert.InRange(counts.Min(), 750, 1200);
        Assert.InRange(counts.Max(), 800, 1250);
        Assert.Equal(deviceCount, counts.Sum());
    }

    /// <summary>
    /// 验证稳定哈希不依赖进程随机化，并严格返回配置范围内的分片编号。
    /// </summary>
    [Theory]
    [InlineData("meter-000001", 866)]
    [InlineData("terminal-A-100", 221)]
    [InlineData("中文设备-01", 837)]
    public void ShardResolver_ReturnsStableKnownShard(string deviceId, int expectedShard)
    {
        Assert.Equal(expectedShard, DeviceCommandShardResolver.Resolve(deviceId, 1024));
    }

    /// <summary>
    /// 验证百万设备不会在 DeviceGrain 或每条 CommandGrain 上注册 Timer/Reminder，只有固定分片调度器持有这些能力。
    /// </summary>
    [Fact]
    public void OnlySchedulerGrain_OwnsTimerAndReminderCapability()
    {
        Assert.DoesNotContain(typeof(IRemindable), typeof(DeviceCommandGrain).GetInterfaces());
        Assert.DoesNotContain(
            typeof(DeviceGrain).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => typeof(IGrainTimer).IsAssignableFrom(field.FieldType));
        Assert.Contains(typeof(IRemindable), typeof(DeviceCommandSchedulerGrain).GetInterfaces());
        Assert.Contains(
            typeof(DeviceCommandSchedulerGrain).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => typeof(IGrainTimer).IsAssignableFrom(field.FieldType));
    }

    /// <summary>
    /// 验证非法分片数会在运行前失败，避免位掩码映射造成设备遗漏。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(-2)]
    public void ShardResolver_RejectsNonPowerOfTwoShardCount(int shardCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeviceCommandShardResolver.Resolve("device-1", shardCount));
    }
}
