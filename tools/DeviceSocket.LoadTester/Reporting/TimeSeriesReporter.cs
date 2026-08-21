using System.Text;
using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 周期写入客户端、负载机资源和可选服务端指标，同时覆盖保存异常恢复检查点。
/// </summary>
/// <param name="metrics">整轮测试共享的累计指标。</param>
/// <param name="loadTestOptions">包含运行编号、分片身份、设备数、采样周期和服务端观测配置的完整配置。</param>
/// <param name="localDeviceCount">当前分片实际分配的虚拟设备数量。</param>
/// <param name="outputDirectory">当前分片独占的报告输出目录。</param>
internal sealed class TimeSeriesReporter(
    LoadTestMetrics metrics,
    LoadTestOptions loadTestOptions,
    int localDeviceCount,
    string outputDirectory) : IAsyncDisposable
{
    private readonly ReportingOptions _reporting = loadTestOptions.Reporting;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ResourceSampler _resourceSampler = new();
    private readonly GatewayStatusClient? _gatewayClient = loadTestOptions.ServerObservation.Enabled
        ? new GatewayStatusClient(loadTestOptions.ServerObservation)
        : null;
    private readonly GatewayObservationAccumulator _gatewayObservations = new();
    private Task? _task;

    /// <summary>
    /// 在虚拟设备开始连接前采集服务端基线；未启用服务端观测时直接返回。
    /// </summary>
    /// <param name="cancellationToken">取消基线 HTTP 请求的令牌。</param>
    /// <returns>基线采样完成的异步任务。</returns>
    internal async Task CaptureBaselineAsync(CancellationToken cancellationToken)
    {
        if (_gatewayClient is null)
            return;

        // 在任何虚拟设备开始建连前采样，后续容量指标才能扣除环境中原本存在的连接。
        _gatewayObservations.SetBaseline(await _gatewayClient.SampleAsync(cancellationToken));
    }

    /// <summary>
    /// 启动后台采样循环并创建报告目录；同一实例只应调用一次。
    /// </summary>
    internal void Start()
    {
        Directory.CreateDirectory(outputDirectory);
        _task = RunAsync(_cancellation.Token);
    }

    /// <summary>
    /// 获取测试期间累计的服务端基线和峰值；未启用服务端观测时为空。
    /// </summary>
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

    /// <summary>
    /// 周期采集客户端、进程资源和可选 Gateway 状态，并写入 JSONL 时间序列。
    /// </summary>
    /// <param name="cancellationToken">停止周期采样的令牌。</param>
    /// <returns>采样循环结束的异步任务。</returns>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(outputDirectory, "node-timeseries.jsonl");
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        var serializerOptions = LoadTestConfigurationLoader.CreateSerializerOptions();
        serializerOptions.WriteIndented = false;
        var nextCheckpointAtUtc = DateTimeOffset.MinValue;

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
            if (entry.Metrics.TimestampUtc >= nextCheckpointAtUtc)
            {
                await WriteCheckpointAsync(entry, cancellationToken);
                nextCheckpointAtUtc = entry.Metrics.TimestampUtc.AddSeconds(_reporting.CheckpointIntervalSeconds);
            }
            if (_reporting.WriteConsoleSummary)
            {
                Console.WriteLine(
                    $"[{entry.Metrics.TimestampUtc:HH:mm:ss}] TCP={entry.Metrics.TcpConnected:N0} " +
                    $"在线={entry.Metrics.ProtocolOnline:N0}/{entry.Metrics.ExpectedOnline:N0} " +
                    $"建连={entry.Metrics.ConnectionAttempts:N0} 失败={entry.Metrics.ConnectionFailed:N0} " +
                    $"重连={entry.Metrics.ReconnectsScheduled:N0} API={entry.Metrics.ApiRequestsSucceeded:N0}/{entry.Metrics.ApiRequestsFailed:N0} " +
                    $"离线跳过={entry.Metrics.ApiRequestsSkippedOffline:N0} " +
                    $"读表应答={entry.Metrics.GatherReadingCommandsResponded:N0}/{entry.Metrics.GatherReadingCommandsReceived:N0} " +
                    $"CPU={entry.Resources.CpuPercent:F1}% " +
                    $"内存={entry.Resources.WorkingSetBytes / 1024d / 1024d:F0}MiB");
            }

            await Task.Delay(TimeSpan.FromSeconds(_reporting.SampleIntervalSeconds), cancellationToken);
        }
    }

    /// <summary>
    /// 先写临时文件再原子替换恢复检查点，避免进程中断时破坏上一份完整 JSON。
    /// </summary>
    /// <param name="entry">当前时间序列采样点。</param>
    /// <param name="cancellationToken">取消检查点写入的令牌。</param>
    /// <returns>检查点安全落盘的异步任务。</returns>
    private async Task WriteCheckpointAsync(TimeSeriesEntry entry, CancellationToken cancellationToken)
    {
        var checkpoint = new RunCheckpoint
        {
            RunId = loadTestOptions.Distributed.RunId,
            NodeName = Environment.MachineName,
            ShardIndex = loadTestOptions.Distributed.ShardIndex,
            ShardCount = loadTestOptions.Distributed.ShardCount,
            LocalDeviceCount = localDeviceCount,
            TimestampUtc = entry.Metrics.TimestampUtc,
            Metrics = entry.Metrics,
            Resources = entry.Resources,
            Gateway = entry.Gateway
        };
        var path = Path.Combine(outputDirectory, "run-checkpoint.json");
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(checkpoint, LoadTestConfigurationLoader.CreateSerializerOptions()),
            new UTF8Encoding(false),
            cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
