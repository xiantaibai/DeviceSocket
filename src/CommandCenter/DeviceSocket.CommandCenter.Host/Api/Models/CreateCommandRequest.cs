namespace DeviceSocket.CommandCenter.Host.Api.Models;

/// <summary>
/// 业务方通过 HTTP 创建的设备下发指令。
/// </summary>
public sealed class CreateCommandRequest
{
    /// <summary>目标设备编号。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>协议名称，必须与设备当前连接协议一致。</summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>完整、已校验的十六进制协议报文。</summary>
    public string Hex { get; set; } = string.Empty;

    /// <summary>调用方指定的 MsgId；为空时由 CommandCenter 生成。</summary>
    public string? MsgId { get; set; }
}
