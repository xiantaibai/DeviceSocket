namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示全体负载节点共享的虚拟设备编号空间。
/// </summary>
public sealed class DeviceOptions
{
    public int Count { get; set; } = 100;
    public long AddressStart { get; set; } = 1;
    public string AdministrativeDivision { get; set; } = "3301";
    public double DuplicateIdentityPercentage { get; set; }
}
