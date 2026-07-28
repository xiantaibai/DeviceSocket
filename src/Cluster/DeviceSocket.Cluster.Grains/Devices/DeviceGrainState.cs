using DeviceSocket.Cluster.Contracts.Devices;
using Orleans;

namespace DeviceSocket.Cluster.Grains.Devices;

/// <summary>
/// 设备 Grain 的持久化状态；当前 Demo 使用内存提供程序，后续可以替换 Redis 或数据库实现。
/// </summary>
[GenerateSerializer]
public sealed class DeviceGrainState
{
    /// <summary>设备当前路由。</summary>
    [Id(0)] public DeviceRouteSnapshot Route { get; set; } = new();
}
