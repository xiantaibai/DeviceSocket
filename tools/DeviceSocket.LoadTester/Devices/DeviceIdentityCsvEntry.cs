namespace DeviceSocket.LoadTester.Devices;

/// <summary>
/// 表示真实设备映射 CSV 中一行经过规范化的电表主键和376.1集中器身份。
/// </summary>
/// <param name="GlobalOrdinal">数据行从零开始的全局序号，用于多负载节点稳定分片。</param>
/// <param name="MeterId">老业务数据库中真实存在的电表主键。</param>
/// <param name="DeviceId">规范格式的376.1设备标识，例如 <c>3761:2608-10001</c>。</param>
/// <param name="AdministrativeDivision">四位行政区划码。</param>
/// <param name="TerminalAddress">协议中的16位终端地址。</param>
internal sealed record DeviceIdentityCsvEntry(
    int GlobalOrdinal,
    int MeterId,
    string DeviceId,
    string AdministrativeDivision,
    ushort TerminalAddress);
