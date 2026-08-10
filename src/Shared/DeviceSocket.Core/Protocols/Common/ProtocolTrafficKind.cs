namespace DeviceSocket.Core.Protocols.Common;

/// <summary>
/// 描述协议帧在通讯观测中的业务类型，用于在生成原始十六进制文本前执行低成本日志策略判断。
/// </summary>
public enum ProtocolTrafficKind
{
    /// <summary>普通业务报文。</summary>
    Business,

    /// <summary>设备上行心跳报文。</summary>
    Heartbeat,

    /// <summary>登录、退出等链路管理报文。</summary>
    LinkManagement,

    /// <summary>登录、退出等链路管理报文的下行确认。</summary>
    LinkConfirmation,

    /// <summary>上行心跳对应的下行确认。</summary>
    HeartbeatConfirmation
}
