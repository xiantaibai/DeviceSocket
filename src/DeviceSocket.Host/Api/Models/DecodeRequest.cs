namespace DeviceSocket.Host.Api.Models;

/// <summary>
/// 协议解析调试接口的请求模型。
/// </summary>
public sealed record DecodeRequest(string Protocol, string Hex);
