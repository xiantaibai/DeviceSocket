using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Reporting;

/// <summary>
/// 校验并合并同一轮测试的全部负载分片报告。
/// </summary>
internal static class ReportMerger
{
    /// <summary>
    /// 读取、校验并合并指定目录中的全部节点报告。
    /// </summary>
    /// <param name="inputDirectory">包含各分片 `node-summary.json` 的目录。</param>
    /// <param name="outputDirectory">汇总报告输出目录。</param>
    /// <param name="cancellationToken">取消读取或写入的令牌。</param>
    /// <returns>完成验收计算的汇总报告。</returns>
    internal static async Task<AggregateReport> MergeAsync(
        string inputDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(inputDirectory);
        if (!Directory.Exists(input))
            throw new DirectoryNotFoundException($"报告输入目录不存在：{input}");

        var paths = Directory.GetFiles(input, "node-summary.json", SearchOption.AllDirectories);
        if (paths.Length == 0)
            throw new InvalidOperationException("输入目录中没有 node-summary.json。");

        var reports = new List<NodeReport>(paths.Length);
        var serializerOptions = LoadTestConfigurationLoader.CreateSerializerOptions();
        foreach (var path in paths)
        {
            await using var stream = File.OpenRead(path);
            var report = await JsonSerializer.DeserializeAsync<NodeReport>(stream, serializerOptions, cancellationToken)
                ?? throw new InvalidDataException($"节点报告为空：{path}");
            reports.Add(report);
        }

        ValidateReports(reports);
        var metrics = MetricSnapshotAggregator.Merge(reports.Select(item => item.Metrics));
        var first = reports[0];
        var gatewayObservation = MergeGatewayObservations(reports);
        var acceptance = AcceptanceEvaluator.Evaluate(
            metrics,
            first.Options.Acceptance,
            (int)metrics.ExpectedOnline,
            gatewayObservation);
        var aggregate = new AggregateReport
        {
            RunId = first.RunId,
            StartedAtUtc = reports.Min(item => item.StartedAtUtc),
            EndedAtUtc = reports.Max(item => item.EndedAtUtc),
            NodeCount = reports.Count,
            DeviceCount = reports.Sum(item => item.LocalDeviceCount),
            Metrics = metrics,
            GatewayObservation = gatewayObservation,
            Acceptance = acceptance,
            Nodes = reports.OrderBy(item => item.ShardIndex).Select(item => item.NodeName).ToArray()
        };
        await AggregateReportWriter.WriteAsync(aggregate, Path.GetFullPath(outputDirectory), cancellationToken);
        return aggregate;
    }

    /// <summary>
    /// 校验分片数量、身份、目标地址和影响验收结论的配置完全一致。
    /// </summary>
    /// <param name="reports">待合并的节点报告。</param>
    private static void ValidateReports(IReadOnlyList<NodeReport> reports)
    {
        var first = reports[0];
        if (reports.Any(item => item.SchemaVersion != 1))
            throw new InvalidDataException("节点报告的 SchemaVersion 不受当前合并器支持。");
        if (reports.Any(item => !string.Equals(item.RunId, first.RunId, StringComparison.Ordinal)))
            throw new InvalidDataException("不能合并 RunId 不同的节点报告。");
        if (reports.Any(item => item.ShardCount != first.ShardCount))
            throw new InvalidDataException("节点报告的 ShardCount 不一致。");
        if (reports.Select(item => item.ShardIndex).Distinct().Count() != reports.Count)
            throw new InvalidDataException("同一分片存在重复节点报告。");
        if (reports.Count != first.ShardCount)
            throw new InvalidDataException($"报告不完整：期望 {first.ShardCount} 个分片，实际 {reports.Count} 个。");
        if (reports.Any(item =>
                item.ShardIndex < 0 ||
                item.ShardIndex >= item.ShardCount ||
                item.Options.Distributed.ShardIndex != item.ShardIndex ||
                item.Options.Distributed.ShardCount != item.ShardCount ||
                !string.Equals(item.Options.Distributed.RunId, item.RunId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("节点报告的外层分片信息与内嵌执行配置不一致。");
        }
        if (reports.Any(item => item.Options.Target.Protocol != first.Options.Target.Protocol ||
                                !string.Equals(item.Options.Target.Host, first.Options.Target.Host, StringComparison.OrdinalIgnoreCase) ||
                                item.Options.Target.Port != first.Options.Target.Port))
        {
            throw new InvalidDataException("节点报告的目标协议或地址不一致。");
        }

        var expectedConfiguration = CreateConfigurationSignature(first);
        if (reports.Skip(1).Any(item =>
                !string.Equals(CreateConfigurationSignature(item), expectedConfiguration, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("节点报告的设备空间、负载场景或验收配置不一致。");
        }
        if (reports.Any(item => item.LocalDeviceCount <= 0) ||
            reports.Sum(item => item.LocalDeviceCount) != first.Options.Devices.Count)
        {
            throw new InvalidDataException("节点报告的本地设备数之和与全局设备总数不一致。");
        }
    }

    /// <summary>
    /// 为影响流量和验收的配置生成稳定 JSON 签名。
    /// </summary>
    /// <param name="report">节点报告。</param>
    /// <returns>用于跨分片相等比较的配置 JSON。</returns>
    private static string CreateConfigurationSignature(NodeReport report)
    {
        // 分片编号和输出路径允许每台负载机不同，其余影响流量或验收结论的配置必须完全一致。
        var options = report.Options;
        return JsonSerializer.Serialize(
            new
            {
                options.Devices,
                options.Connection,
                options.Heartbeat,
                options.Workload,
                options.Reconnect,
                options.Scenario,
                options.Acceptance,
                ServerObservationEnabled = options.ServerObservation.Enabled,
                options.Reporting.SampleIntervalSeconds,
                options.Distributed.StartAtUtc
            },
            LoadTestConfigurationLoader.CreateSerializerOptions());
    }

    /// <summary>
    /// 合并多台负载机对同一个 Gateway 的重复观测，累计采样数并对服务端峰值取最大值。
    /// </summary>
    /// <param name="reports">全部节点报告。</param>
    /// <returns>合并后的 Gateway 观测；没有启用服务端采样时返回 null。</returns>
    private static GatewayObservationSummary? MergeGatewayObservations(IEnumerable<NodeReport> reports)
    {
        var observations = reports
            .Where(item => item.GatewayObservation is not null)
            .Select(item => item.GatewayObservation!)
            .ToArray();
        if (observations.Length == 0)
            return null;
        return new GatewayObservationSummary(
            observations.Sum(item => item.Samples),
            observations.Sum(item => item.UnavailableSamples),
            MinimumNullable(observations.Select(item => item.BaselineTcpConnections)),
            MinimumNullable(observations.Select(item => item.BaselineAuthenticatedDevices)),
            observations.Max(item => item.MaximumTcpConnections),
            observations.Max(item => item.MaximumAuthenticatedDevices),
            observations.Max(item => item.MaximumAdditionalTcpConnections),
            observations.Max(item => item.MaximumAdditionalAuthenticatedDevices),
            observations.Where(item => item.Last is not null)
                .Select(item => item.Last)
                .OrderByDescending(item => item!.TimestampUtc)
                .FirstOrDefault())
        {
            MaximumProcessCpuPercent = observations.Max(item => item.MaximumProcessCpuPercent),
            MaximumProcessWorkingSetBytes = observations.Max(item => item.MaximumProcessWorkingSetBytes),
            MaximumManagedHeapBytes = observations.Max(item => item.MaximumManagedHeapBytes),
            MaximumThreadPoolPendingWorkItems = observations.Max(item => item.MaximumThreadPoolPendingWorkItems),
            MaximumPeakPendingSendMessages = observations.Max(item => item.MaximumPeakPendingSendMessages),
            MaximumPeakPendingSendBytes = observations.Max(item => item.MaximumPeakPendingSendBytes),
            MaximumSessionScanDurationMilliseconds = observations.Max(item => item.MaximumSessionScanDurationMilliseconds),
            // 多台负载机采样的是同一个 Gateway 累计计数，合并时取最大值，不能把重复观测相加。
            AdditionalApplicationLogDropped = observations.Max(item => item.AdditionalApplicationLogDropped),
            AdditionalUpstreamLogDropped = observations.Max(item => item.AdditionalUpstreamLogDropped),
            AdditionalDownstreamLogDropped = observations.Max(item => item.AdditionalDownstreamLogDropped),
            AdditionalHeartbeatLogSuppressed = observations.Max(item => item.AdditionalHeartbeatLogSuppressed)
        };
    }

    /// <summary>
    /// 计算可用可空整数中的最小值，全部为空时保持空值语义。
    /// </summary>
    /// <param name="values">可空整数序列。</param>
    /// <returns>最小非空值；没有非空值时返回 null。</returns>
    private static long? MinimumNullable(IEnumerable<long?> values)
    {
        var available = values.Where(item => item.HasValue).Select(item => item!.Value).ToArray();
        return available.Length == 0 ? null : available.Min();
    }
}
