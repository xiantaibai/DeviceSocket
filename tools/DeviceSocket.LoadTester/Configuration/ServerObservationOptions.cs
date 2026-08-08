namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示可选 Gateway 状态 API 采样地址和请求超时，用于核对客户端与服务端在线数量。
/// </summary>
public sealed class ServerObservationOptions
{
    public bool Enabled { get; set; }
    public string StatusUrl { get; set; } = "http://127.0.0.1:5080/api/status/connections";
    public int TimeoutSeconds { get; set; } = 2;
}
