using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Devices;

/// <summary>
/// 保存一个虚拟设备在所有负载分片之间稳定且不重复的协议身份。
/// </summary>
public sealed record VirtualDeviceDescriptor(
    int GlobalOrdinal,
    LoadProtocol Protocol,
    string DeviceId,
    string AdministrativeDivision,
    ushort TerminalAddress,
    string MeterAddress,
    int RandomSeed);
