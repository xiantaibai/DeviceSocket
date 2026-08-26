using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>
/// 从顺序主键或 CSV 加载电表与虚拟集中器映射，并按当前分片筛选。
/// </summary>
internal static class GatherDeviceMeterMappingLoader
{
    /// <summary>
    /// 为当前分片生成 HTTP 指令目标映射。
    /// </summary>
    /// <param name="options">完整负载配置。</param>
    /// <param name="devices">当前分片负责的虚拟设备。</param>
    /// <returns>按全局设备序号排序的本地映射。</returns>
    /// <exception cref="InvalidDataException">CSV 语法、主键或重复映射无效。</exception>
    internal static IReadOnlyList<GatherDeviceMeterMapping> Load(
        LoadTestOptions options,
        IReadOnlyList<VirtualDeviceDescriptor> devices)
    {
        if (options.CommandWorkload.MeterIdStart is { } meterIdStart)
        {
            return devices
                .Select(device => new GatherDeviceMeterMapping(
                    device,
                    checked(meterIdStart + device.GlobalOrdinal)))
                .ToArray();
        }

        var path = !string.IsNullOrWhiteSpace(options.CommandWorkload.DeviceMappingFile)
            ? options.CommandWorkload.DeviceMappingFile
            : options.Devices.DeviceMappingFile;
        var allowZeroTerminalAddress =
            options.Target.Protocol == LoadProtocol.QGdw3761 &&
            options.Target.CompatibilityProfile == ServerCompatibilityProfile.GatherTcpCenter;
        return LoadCsv(path!, devices, allowZeroTerminalAddress);
    }

    /// <summary>
    /// 读取共享 CSV，并只保留 deviceId 属于当前分片的行。
    /// </summary>
    /// <param name="path">包含 <c>meterId,deviceId</c> 两列的 CSV 绝对路径。</param>
    /// <param name="devices">当前分片设备。</param>
    /// <param name="allowZeroTerminalAddress">是否接受 Gather 老库真实存在的特殊终端地址 <c>00000</c>。</param>
    /// <returns>按全局设备序号排序的本地映射。</returns>
    private static IReadOnlyList<GatherDeviceMeterMapping> LoadCsv(
        string path,
        IReadOnlyList<VirtualDeviceDescriptor> devices,
        bool allowZeroTerminalAddress)
    {
        var localDevices = devices.ToDictionary(
            device => device.DeviceId,
            StringComparer.OrdinalIgnoreCase);
        return DeviceIdentityCsvLoader.Load(path, allowZeroTerminalAddress)
            .Where(entry => localDevices.ContainsKey(entry.DeviceId))
            .Select(entry => new GatherDeviceMeterMapping(localDevices[entry.DeviceId], entry.MeterId))
            .OrderBy(item => item.Device.GlobalOrdinal)
            .ToArray();
    }
}
