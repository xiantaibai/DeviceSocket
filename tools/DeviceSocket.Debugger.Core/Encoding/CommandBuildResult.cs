namespace DeviceSocket.Debugger.Core.Encoding;

/// <summary>
/// 表示一条指令成功生成后的原始报文和说明。
/// </summary>
public sealed record CommandBuildResult(byte[] Bytes, string Summary);
