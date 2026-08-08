using System.Text;
using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

internal sealed class TimeSeriesReporter(
    LoadTestMetrics metrics,
    ReportingOptions options,
    ServerObservationOptions serverObservation,
    string outputDirectory) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ResourceSampler _resourceSampler = new();
    private readonly GatewayStatusClient? _gatewayClient = serverObservation.Enabled
        ? new GatewayStatusClient(serverObservation)
        : null;
    private readonly GatewayObservationAccumulator _gatewayObservations = new();
    private Task? _task;

    internal async Task CaptureBaselineAsync(CancellationToken cancellationToken)
    {
        if (_gatewayClient is null)
            return;

        // 在任何虚拟设备开始建连前采样，后续容量指标才能扣除环境中原本存在的连接。
        _gatewayObservations.SetBaseline(await _gatewayClient.SampleAsync(cancellationToken));
    }

    internal void Start()
    {
        Directory.CreateDirectory(outputDirectory);
        _task = RunAsync(_cancellation.Token);
    }

    internal GatewayObservationSummary? GatewaySummary =>
        _gatewayClient is null ? null : _gatewayObservations.Snapshot();

    /// <summary>
    /// 停止周期采样并等待当前 JSONL 写入安全完成。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_task is not null)
        {
            try
            {
                await _task;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _resourceSampler.Dispose();
        (_gatewayClient as IDisposable)?.Dispose();
        _cancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(outputDirectory, "node-timeseries.jsonl");
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        var serializerOptions = LoadTestConfigurationLoader.CreateSerializerOptions();
        serializerOptions.WriteIndented = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var gateway = _gatewayClient is null
                ? null
                : await _gatewayClient.SampleAsync(cancellationToken);
            if (gateway is not null)
                _gatewayObservations.Record(gateway);
            var entry = new TimeSeriesEntry(
                metrics.Snapshot(DateTimeOffset.UtcNow),
                _resourceSampler.Sample(),
                gateway);
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry, serializerOptions));
            await writer.FlushAsync(cancellationToken);
            if (options.WriteConsoleSummary)
            {
                Console.WriteLine(
                    $"[{entry.Metrics.TimestampUtc:HH:mm:ss}] TCP={entry.Metrics.TcpConnected:N0} " +
                    $"在线={entry.Metrics.ProtocolOnline:N0}/{entry.Metrics.ExpectedOnline:N0} " +
                    $"建连={entry.Metrics.ConnectionAttempts:N0} 失败={entry.Metrics.ConnectionFailed:N0} " +
                    $"重连={entry.Metrics.ReconnectsScheduled:N0} CPU={entry.Resources.CpuPercent:F1}% " +
                    $"内存={entry.Resources.WorkingSetBytes / 1024d / 1024d:F0}MiB");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.SampleIntervalSeconds), cancellationToken);
        }
    }
}
