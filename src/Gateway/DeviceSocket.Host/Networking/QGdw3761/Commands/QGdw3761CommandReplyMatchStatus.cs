namespace DeviceSocket.Host.Networking.QGdw3761.Commands;

/// <summary>
/// 376.1 上行确认与当前 Session 可靠命令关联后的分类结果。
/// </summary>
public enum QGdw3761CommandReplyMatchStatus
{
    /// <summary>已经完整匹配并取得一次发布权。</summary>
    Matched = 0,

    /// <summary>当前 Session 没有待关联的逻辑命令。</summary>
    NoPendingCommand = 1,

    /// <summary>报文不是符合硬件约定的 AFN=00H/F3 从动确认。</summary>
    InvalidConfirmation = 2,

    /// <summary>PSEQ、设备或被确认的 AFN/DA/DT 与当前命令不一致。</summary>
    CorrelationMismatch = 3,

    /// <summary>报文已超过本次尝试的回复窗口，不再推进命令状态。</summary>
    LateReply = 4,

    /// <summary>逻辑命令已经超过整体到期时间，关联已被删除。</summary>
    CommandExpired = 5,

    /// <summary>同一回复正在发布或已经被当前处理器占用。</summary>
    DuplicateReply = 6
}
