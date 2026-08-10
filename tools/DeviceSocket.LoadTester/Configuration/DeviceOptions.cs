namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示全体负载节点共享的虚拟设备编号空间。
/// </summary>
public sealed class DeviceOptions
{
    /// <summary>本轮全部负载分片合计的虚拟连接数量；默认 100 条，必须大于零。</summary>
    public int Count { get; set; } = 100;

    /// <summary>协议设备地址的起始值；默认 1，376.1 下表示首个行政区划内的终端地址。</summary>
    public long AddressStart { get; set; } = 1;

    /// <summary>376.1 起始行政区划的 4 位 BCD（Binary-Coded Decimal）文本；默认 `3301`。</summary>
    public string AdministrativeDivision { get; set; } = "3301";

    /// <summary>376.1 终端地址超过 65535 时是否递增行政区划并从终端地址 1 继续分配；默认 false。</summary>
    public bool SpanAdministrativeDivisions { get; set; }

    /// <summary>复用协议设备身份以制造重复登录的连接比例；默认 0，取值 0～100。</summary>
    public double DuplicateIdentityPercentage { get; set; }
}
