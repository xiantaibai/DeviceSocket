namespace DeviceSocket.Host.Networking.QGdw3761.Commands;

/// <summary>
/// 通过 Session、PSEQ 和原数据单元标识匹配出的 Orleans 命令回复上下文。
/// </summary>
public sealed record QGdw3761CommandReplyMatch
{
    /// <summary>防止旧处理结果修改新关联的内部版本。</summary>
    public required long Version { get; init; }

    /// <summary>产生设备回复的 TCP Session 编号。</summary>
    public required string SessionId { get; init; }

    /// <summary>目标设备编号。</summary>
    public required string DeviceId { get; init; }

    /// <summary>对应的全局逻辑命令编号。</summary>
    public required string MsgId { get; init; }

    /// <summary>当前正式发送次数。</summary>
    public required int AttemptNo { get; init; }

    /// <summary>命令绑定的 Orleans 连接代次。</summary>
    public required long SessionEpoch { get; init; }

    /// <summary>跨服务调用链路编号。</summary>
    public required string TraceId { get; init; }

    /// <summary>终端 ERR=0 时为 true，非零时为 false。</summary>
    public required bool Success { get; init; }

    /// <summary>终端 AFN=00H/F3 返回的原始 ERR 值。</summary>
    public required byte ErrorCode { get; init; }
}
