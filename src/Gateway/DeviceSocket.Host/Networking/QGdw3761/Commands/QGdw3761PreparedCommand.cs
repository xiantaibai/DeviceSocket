namespace DeviceSocket.Host.Networking.QGdw3761.Commands;

/// <summary>
/// Gateway 为可靠下发写入 PSEQ、CON 和新校验和后的 376.1 报文及关联信息。
/// </summary>
public sealed record QGdw3761PreparedCommand
{
    /// <summary>实际写入设备 Socket 的完整报文。</summary>
    public required byte[] Payload { get; init; }

    /// <summary>Gateway 为当前逻辑命令分配的低 4 位 PSEQ。</summary>
    public required byte Sequence { get; init; }

    /// <summary>本次尝试接受设备协议回复的绝对截止时间。</summary>
    public required DateTimeOffset ReplyDeadline { get; init; }
}
