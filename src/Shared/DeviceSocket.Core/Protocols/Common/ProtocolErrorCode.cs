namespace DeviceSocket.Core.Protocols.Common;

/// <summary>
/// 协议帧校验和解析过程中可识别的错误类型。
/// </summary>
public enum ProtocolErrorCode
{
    None,
    GarbageBeforeFrame,
    InvalidStart,
    InvalidSecondStart,
    InvalidLength,
    LengthMismatch,
    UnsupportedProtocolIdentifier,
    FrameTooLarge,
    InvalidChecksum,
    InvalidEnd,
    InvalidAddress,
    InvalidData,
    UnsupportedMessage
}
