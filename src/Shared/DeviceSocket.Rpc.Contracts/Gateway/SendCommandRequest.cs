using MessagePack;

namespace DeviceSocket.Rpc.Contracts.Gateway;

/// <summary>
/// Cluster Silo 发往指定 Gateway 的设备消息请求。
/// </summary>
[MessagePackObject]
public sealed class SendCommandRequest
{
    /// <summary>全局消息编号；重试时必须保持不变，用于幂等处理和日志关联。</summary>
    [Key(0)]
    public string MsgId { get; set; } = string.Empty;

    /// <summary>目标设备编号。</summary>
    [Key(1)]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备当前所在网关的 Session 编号。</summary>
    [Key(2)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Orleans 为本次登录分配的会话世代号。</summary>
    [Key(3)]
    public long SessionEpoch { get; set; }

    /// <summary>协议名称，例如 QGdw3761 或 Dlt6452007。</summary>
    [Key(4)]
    public string Protocol { get; set; } = string.Empty;

    /// <summary>已经完成协议编码和校验的原始报文字节。</summary>
    [Key(5)]
    public byte[] Payload { get; set; } = [];

    /// <summary>跨服务调用链路编号。</summary>
    [Key(6)]
    public string TraceId { get; set; } = string.Empty;

    /// <summary>
    /// 同一可靠消息的正式发送次数；RPC 结果不确定时保持不变，设备确认超时后的正式重发才递增。
    /// </summary>
    [Key(7)]
    public int AttemptNo { get; set; } = 1;

    /// <summary>是否必须收到设备协议确认；为 false 时 Gateway 不创建协议应答关联。</summary>
    [Key(8)]
    public bool RequireDeviceConfirmation { get; set; }

    /// <summary>完整报文写入 Socket 后允许设备回复的最长时间。</summary>
    [Key(9)]
    public TimeSpan ResponseTimeout { get; set; }

    /// <summary>逻辑命令停止发送和接受设备回复的绝对到期时间。</summary>
    [Key(10)]
    public DateTimeOffset ExpiresAt { get; set; }
}
