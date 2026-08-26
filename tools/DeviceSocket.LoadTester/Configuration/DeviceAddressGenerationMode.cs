namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 指定虚拟设备终端地址的生成方式。
/// </summary>
public enum DeviceAddressGenerationMode
{
    /// <summary>
    /// 从 <c>devices.addressStart</c> 开始连续递增，适合数据库中按连续规则预置设备的场景。
    /// </summary>
    Sequential,

    /// <summary>
    /// 在 <c>devices.addressStart</c> 到 <c>devices.addressEnd</c> 的闭区间内无重复抽取，适合随机选取已预置设备的场景。
    /// </summary>
    Random,

    /// <summary>
    /// 从 <c>devices.deviceMappingFile</c> 的 <c>meterId,deviceId</c> 行读取真实376.1设备身份，适合数据库设备编号不连续的场景；Gather 老服务兼容配置还允许读取真实特殊终端地址 <c>00000</c>。
    /// </summary>
    Csv
}
