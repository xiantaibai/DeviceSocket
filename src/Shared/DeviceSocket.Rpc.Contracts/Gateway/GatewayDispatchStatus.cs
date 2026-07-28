namespace DeviceSocket.Rpc.Contracts.Gateway;

/// <summary>
/// 网关处理下发请求后的确定性结果，用于调用方判断是否可以重试。
/// </summary>
public enum GatewayDispatchStatus
{
    /// <summary>网关已经将完整报文写入当前设备 Socket。</summary>
    WrittenToSocket = 0,

    /// <summary>目标设备不在当前网关在线。</summary>
    SessionNotFound = 1,

    /// <summary>请求携带的 Session 或世代号已经过期。</summary>
    StaleSession = 2,

    /// <summary>报文或必要标识不合法。</summary>
    InvalidCommand = 3,

    /// <summary>网关当前正在停止，不能接受新的指令。</summary>
    GatewayStopping = 4,

    /// <summary>相同 MsgId 已经处理过，本次未重复发送。</summary>
    Duplicate = 5,

    /// <summary>写入设备 Socket 时发生异常。</summary>
    SendFailed = 6
}
