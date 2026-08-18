using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Tests.Reporting;

/// <summary>
/// 验证多节点报告只合并同一轮完整分片，并正确累计核心指标。
/// </summary>
public sealed class ReportMergerTests
{
    /// <summary>
    /// 验证两个完整分片可以生成统一设备数、连接数和登录数报告。
    /// </summary>
    [Fact]
    public async Task CompleteShards_AreMerged()
    {
        var root = Path.Combine(Path.GetTempPath(), $"device-load-report-{Guid.NewGuid():N}");
        try
        {
            var firstPath = Path.Combine(root, "shard-000");
            var secondPath = Path.Combine(root, "shard-001");
            await NodeReportWriter.WriteAsync(CreateReport(0, "node-a"), firstPath, CancellationToken.None);
            await NodeReportWriter.WriteAsync(CreateReport(1, "node-b"), secondPath, CancellationToken.None);

            var aggregate = await ReportMerger.MergeAsync(root, Path.Combine(root, "aggregate"), CancellationToken.None);

            Assert.Equal(2, aggregate.NodeCount);
            Assert.Equal(10, aggregate.DeviceCount);
            Assert.Equal(10, aggregate.Metrics.ProtocolOnline);
            Assert.Equal(10, aggregate.Metrics.LoginSucceeded);
            Assert.Equal(LoadTestRunStatus.Passed, aggregate.RunStatus);
            Assert.True(File.Exists(Path.Combine(root, "aggregate", "aggregate-summary.json")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 验证任一分片因在线门槛不足跳过 HTTP 阶段时，汇总报告不会只根据累计指标误判为通过。
    /// </summary>
    [Fact]
    public async Task SkippedNodeStatus_IsPropagatedToAggregate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"device-load-report-{Guid.NewGuid():N}");
        try
        {
            var first = CreateReport(0, "node-a");
            var second = CreateReport(1, "node-b") with
            {
                RunStatus = LoadTestRunStatus.CompletedWithSkippedPhases,
                FailureReason = "在线峰值未达到阶段门槛。"
            };
            await NodeReportWriter.WriteAsync(first, Path.Combine(root, "shard-000"), CancellationToken.None);
            await NodeReportWriter.WriteAsync(second, Path.Combine(root, "shard-001"), CancellationToken.None);

            var aggregate = await ReportMerger.MergeAsync(root, Path.Combine(root, "aggregate"), CancellationToken.None);

            Assert.Equal(LoadTestRunStatus.CompletedWithSkippedPhases, aggregate.RunStatus);
            Assert.Contains(aggregate.NodeOutcomes, item =>
                item.ShardIndex == 1 && item.Reason == "在线峰值未达到阶段门槛。");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 验证同一 RunId 下设备地址空间不同的节点报告会被拒绝，避免生成看似完整但不可比较的汇总。
    /// </summary>
    [Fact]
    public async Task MismatchedDeviceConfiguration_IsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"device-load-report-{Guid.NewGuid():N}");
        try
        {
            var first = CreateReport(0, "node-a");
            var second = CreateReport(1, "node-b");
            second.Options.Devices.AddressStart++;
            await NodeReportWriter.WriteAsync(first, Path.Combine(root, "shard-000"), CancellationToken.None);
            await NodeReportWriter.WriteAsync(second, Path.Combine(root, "shard-001"), CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ReportMerger.MergeAsync(root, Path.Combine(root, "aggregate"), CancellationToken.None));

            Assert.Contains("设备空间", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 验证新老服务兼容配置不同的分片报告不能合并，避免混淆两种确认语义的结果。
    /// </summary>
    [Fact]
    public async Task MismatchedCompatibilityProfile_IsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"device-load-report-{Guid.NewGuid():N}");
        try
        {
            var first = CreateReport(0, "node-a");
            var second = CreateReport(1, "node-b");
            second.Options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
            await NodeReportWriter.WriteAsync(first, Path.Combine(root, "shard-000"), CancellationToken.None);
            await NodeReportWriter.WriteAsync(second, Path.Combine(root, "shard-001"), CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ReportMerger.MergeAsync(root, Path.Combine(root, "aggregate"), CancellationToken.None));

            Assert.Contains("兼容配置", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static NodeReport CreateReport(int shardIndex, string nodeName)
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 10;
        options.Distributed.RunId = "merge-test";
        options.Distributed.ShardCount = 2;
        options.Distributed.ShardIndex = shardIndex;
        var now = DateTimeOffset.UtcNow;
        var metrics = new MetricSnapshot
        {
            TimestampUtc = now,
            ExpectedOnline = 5,
            TcpConnected = 5,
            ProtocolOnline = 5,
            ConnectionAttempts = 5,
            ConnectionSucceeded = 5,
            LoginAttempts = 5,
            LoginSucceeded = 5
        };
        return new NodeReport
        {
            RunId = "merge-test",
            NodeName = nodeName,
            ShardIndex = shardIndex,
            ShardCount = 2,
            LocalDeviceCount = 5,
            StartedAtUtc = now.AddSeconds(-1),
            EndedAtUtc = now,
            Options = options,
            Metrics = metrics,
            Resources = new ResourceSnapshot(now, 0, 0, 0, 0, 0, 0, 0, 0),
            Phases = [],
            RunStatus = LoadTestRunStatus.Passed,
            Acceptance = new AcceptanceResult(true, []),
            PreflightWarnings = []
        };
    }
}
