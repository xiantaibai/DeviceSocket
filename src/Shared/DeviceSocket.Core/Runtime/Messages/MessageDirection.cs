namespace DeviceSocket.Core.Runtime.Messages;

/// <summary>
/// 设备消息相对于通信网关的传输方向。
/// </summary>
public enum MessageDirection
{
    /// <summary>设备发送给服务器的上报消息。</summary>
    Upstream = 0,

    /// <summary>服务器发送给设备的下发消息。</summary>
    Downstream = 1
}
