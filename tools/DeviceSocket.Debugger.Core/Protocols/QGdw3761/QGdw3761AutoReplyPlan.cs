namespace DeviceSocket.Debugger.Core.Protocols.QGdw3761;

/// <summary>
/// 描述一条服务端下行指令对应的自动回复报文及回复看板摘要。
/// </summary>
public sealed record QGdw3761AutoReplyPlan
{
    /// <summary>获取回复类型，用于区分业务数据和协议确认。</summary>
    public required QGdw3761AutoReplyKind Kind { get; init; }

    /// <summary>获取可直接写入 TCP 连接的完整回复报文。</summary>
    public required byte[] ResponseBytes { get; init; }

    /// <summary>获取下行请求的设备编号。</summary>
    public required string DeviceId { get; init; }

    /// <summary>获取下行请求的 AFN、FN、PN 和 PSEQ 摘要。</summary>
    public required string RequestInstruction { get; init; }

    /// <summary>获取自动回复的指令类型摘要。</summary>
    public required string ResponseInstruction { get; init; }

    /// <summary>获取用于发送日志和回复看板的中文说明。</summary>
    public required string Summary { get; init; }
}
