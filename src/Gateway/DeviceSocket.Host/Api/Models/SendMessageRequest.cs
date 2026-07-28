namespace DeviceSocket.Host.Api.Models;

/// <summary>
/// 指定设备原始报文下发接口的请求模型。
/// </summary>
public sealed record SendMessageRequest(string Hex, string? Protocol = null);
