namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示全体负载节点共享的虚拟设备编号空间。
/// </summary>
public sealed class DeviceOptions
{
    /// <summary>
    /// 获取或设置 <c>devices.count</c>；表示本轮全部负载分片合计的虚拟连接数量，单位为条，默认 100，零值无效。
    /// </summary>
    public int Count { get; set; } = 100;

    /// <summary>
    /// 获取或设置 <c>devices.generationMode</c>；默认 <see cref="DeviceAddressGenerationMode.Sequential"/>，决定终端地址按顺序递增或在指定区间内随机抽取。
    /// </summary>
    public DeviceAddressGenerationMode GenerationMode { get; set; } = DeviceAddressGenerationMode.Sequential;

    /// <summary>
    /// 获取或设置 <c>devices.addressStart</c>；默认 1，376.1 下表示首个终端地址，顺序模式从该值递增，随机模式将其作为闭区间下界，零值无效。
    /// </summary>
    public long AddressStart { get; set; } = 1;

    /// <summary>
    /// 获取或设置 <c>devices.addressEnd</c>；单位为协议地址，默认不设置，零值无效；仅随机模式必须设置并作为随机抽取闭区间上界。
    /// </summary>
    public long? AddressEnd { get; set; }

    /// <summary>
    /// 获取或设置 <c>devices.administrativeDivision</c>；默认 <c>3301</c>，376.1 下必须是数据库真实存在的 4 位 BCD（Binary-Coded Decimal）行政区划码。
    /// </summary>
    public string AdministrativeDivision { get; set; } = "3301";

    /// <summary>
    /// 获取或设置 <c>devices.spanAdministrativeDivisions</c>；默认 false，表示固定四位行政区划；true 仅供超大顺序压测跨区划使用，随机模式不支持。
    /// </summary>
    public bool SpanAdministrativeDivisions { get; set; }

    /// <summary>
    /// 获取或设置 <c>devices.duplicateIdentityPercentage</c>；单位为百分比，默认 0 表示每条连接使用独立身份，取值 0～100，用于制造重复登录。
    /// </summary>
    public double DuplicateIdentityPercentage { get; set; }
}
