namespace DeviceSocket.Core.Protocols.Common;

/// <summary>
/// 所有协议帧向业务层暴露的统一只读视图。
/// </summary>
public interface IProtocolFrame
{
    DeviceProtocol Protocol { get; }

    bool IsValid { get; }

    ProtocolError? Error { get; }

    string? DeviceId { get; }

    byte[] Raw { get; }

    string Summary { get; }
}
