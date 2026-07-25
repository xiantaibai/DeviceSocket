using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Debugger.Core.Decoding;

/// <summary>
/// 表示调试接收缓冲区的一次协议帧读取结果。
/// </summary>
public sealed record ProtocolFrameReadResult(bool NeedMoreData, int Consumed, IProtocolFrame? Frame);
