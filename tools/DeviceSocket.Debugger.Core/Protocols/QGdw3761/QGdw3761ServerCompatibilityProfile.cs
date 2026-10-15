namespace DeviceSocket.Debugger.Core.Protocols.QGdw3761;

/// <summary>
/// 表示 Q/GDW 376.1 调试连接所面对的服务端实现，用于选择链路确认和请求关联兼容规则。
/// </summary>
public enum QGdw3761ServerCompatibilityProfile
{
    /// <summary>采用标准序号关联规则，登录和心跳默认不强制请求确认。</summary>
    Standard,

    /// <summary>兼容 Gather 老服务固定 RSEQ 和 AFN=00H/F1 通用确认行为。</summary>
    GatherTcpCenter,

    /// <summary>兼容 GatherV4，登录、退出和心跳设置 CON=1，并严格按设备与 PSEQ 关联确认。</summary>
    GatherV4
}
