namespace DeviceSocket.Core.Protocols.Common;

/// <summary>
/// 所有协议帧向业务层暴露的统一只读视图。
/// </summary>
public interface IProtocolFrame
{
    /// <summary>当前帧所属协议。</summary>
    DeviceProtocol Protocol { get; }

    /// <summary>当前帧是否已经通过协议完整性校验。</summary>
    bool IsValid { get; }

    /// <summary>协议校验失败信息；合法帧为空。</summary>
    ProtocolError? Error { get; }

    /// <summary>协议能够解析出的设备编号；地址不完整时为空。</summary>
    string? DeviceId { get; }

    /// <summary>当前完整协议帧的原始字节。</summary>
    byte[] Raw { get; }

    /// <summary>用于观测和调试的协议帧简要描述。</summary>
    string Summary { get; }

    /// <summary>当前帧在通讯日志中的协议无关业务类型。</summary>
    ProtocolTrafficKind TrafficKind { get; }
}
