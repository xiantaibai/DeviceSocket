using System.Net.Http.Json;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Reporting;

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
