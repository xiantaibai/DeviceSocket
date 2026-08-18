using System.Net.Http.Json;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 按报告采样周期调用可选 Gateway 状态接口；采样错误会转换为不可用快照，不会中断设备和 HTTP 压测。
/// </summary>
/// <param name="options">服务端观测地址、启用状态和单次请求超时配置。</param>
internal sealed class GatewayStatusClient(ServerObservationOptions options) : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };

    internal async Task<GatewayStatusSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var snapshot = await _client.GetFromJsonAsync<GatewayStatusSnapshot>(options.StatusUrl, cancellationToken);
            return snapshot is null
                ? new GatewayStatusSnapshot { TimestampUtc = now, Error = "Gateway 状态响应为空。" }
                : snapshot with { TimestampUtc = now, Available = true, Error = null };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            return new GatewayStatusSnapshot
            {
                TimestampUtc = now,
                Available = false,
                Error = exception.Message
            };
        }
    }

    void IDisposable.Dispose() => _client.Dispose();
}
