using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Reporting;

internal static class ReportMerger
{
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
                .FirstOrDefault());
    }

    private static long? MinimumNullable(IEnumerable<long?> values)
    {
        var available = values.Where(item => item.HasValue).Select(item => item!.Value).ToArray();
        return available.Length == 0 ? null : available.Min();
    }
}
