using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>
/// 关联一个虚拟集中器身份与老业务数据库中的真实电表主键。
/// </summary>
/// <param name="Device">当前分片负责的虚拟设备。</param>
/// <param name="MeterId">调用 <c>api/ammeter/reading</c> 使用的真实 meter_id。</param>
internal sealed record GatherDeviceMeterMapping(VirtualDeviceDescriptor Device, int MeterId);
