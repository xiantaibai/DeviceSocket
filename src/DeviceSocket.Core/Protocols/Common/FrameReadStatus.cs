namespace DeviceSocket.Core.Protocols.Common;

/// <summary>
/// 协议拆包器对当前缓冲区的处理状态。
/// </summary>
public enum FrameReadStatus
{
    NeedMoreData,
    PackageReady
}
