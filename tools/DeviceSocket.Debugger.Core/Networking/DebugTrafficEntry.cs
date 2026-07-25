namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 表示调试工具中的一条连接、发送、接收或错误记录。
/// </summary>
public sealed record DebugTrafficEntry(
    DateTimeOffset Timestamp,
    TrafficDirection Direction,
    string Title,
    string RawHex,
    string Details);
