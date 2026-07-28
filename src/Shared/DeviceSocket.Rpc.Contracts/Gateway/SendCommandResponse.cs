using MessagePack;

namespace DeviceSocket.Rpc.Contracts.Gateway;

/// <summary>
/// 网关下发结果；成功只表示已写入 Socket，不等同于设备业务确认。
/// </summary>
[MessagePackObject]
public sealed class SendCommandResponse
{
    /// <summary>与请求一致的全局消息编号。</summary>
    [Key(0)]
    public string MsgId { get; set; } = string.Empty;

    /// <summary>网关处理状态。</summary>
    [Key(1)]
    public GatewayDispatchStatus Status { get; set; }

    /// <summary>便于日志和人工排查的中文结果说明。</summary>
    [Key(2)]
    public string Message { get; set; } = string.Empty;

    /// <summary>实际写入 Socket 的字节数；失败时为零。</summary>
    [Key(3)]
    public int BytesWritten { get; set; }

    /// <summary>处理请求的网关节点编号。</summary>
    [Key(4)]
    public string GatewayNodeId { get; set; } = string.Empty;
}
