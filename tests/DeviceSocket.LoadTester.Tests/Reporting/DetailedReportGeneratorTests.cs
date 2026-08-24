using System.Text;
using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Reporting;
using DeviceSocket.LoadTester.Reporting.Detailed;

namespace DeviceSocket.LoadTester.Tests.Reporting;

/// <summary>
/// 验证离线详细报告能够联合客户端和 TcpCenter 指标计算业务在线追平并生成双受众产物。
/// </summary>
public sealed class DetailedReportGeneratorTests
{
    /// <summary>
    /// 验证服务物理连接先达到目标、业务在线稍后追平时能够计算差值，并生成 HTML、Markdown、JSON、CSV 和 SVG。
    /// </summary>
    [Fact]
    public async Task ClientAndServerMetrics_GenerateBothAudienceReports()
    {
        var root = Path.Combine(Path.GetTempPath(), $"device-detailed-report-{Guid.NewGuid():N}");
        try
        {
            var shard = Path.Combine(root, "shard-000");
            var output = Path.Combine(root, "analysis");
            var startedAt = DateTimeOffset.Parse("2026-08-21T02:30:00Z");
            var endedAt = startedAt.AddMinutes(1);
            await NodeReportWriter.WriteAsync(CreateReport(startedAt, endedAt), shard, CancellationToken.None);
            await WriteClientSeriesAsync(shard, startedAt);
            var serverPath = Path.Combine(shard, "server.jsonl");
            await File.WriteAllTextAsync(serverPath, BuildServerMetrics(), new UTF8Encoding(false));
            var backgroundPath = Path.Combine(root, "background.json");
            await File.WriteAllTextAsync(backgroundPath, BuildBackgroundJson(), new UTF8Encoding(false));

            var analysis = await DetailedReportGenerator.GenerateAsync(
                new DetailedReportGenerationOptions
                {
                    InputDirectory = shard,
                    ServerMetricsPath = serverPath,
                    BackgroundPath = backgroundPath,
                    OutputDirectory = output,
                    Audience = DetailedReportAudience.Both,
                    BaselineInputDirectory = shard,
                    BaselineServerMetricsPath = serverPath,
                    Title = "命令行覆盖标题",
                    ServiceVersion = "cli-version",
                    ServerTimeZoneId = "UTC",
                    TargetDeviceCount = 100
                },
                CancellationToken.None);

            Assert.NotNull(analysis.Server);
            Assert.Equal(100, analysis.Server.MaximumSocketConnections);
            Assert.Equal(100, analysis.Server.MaximumBusinessOnlineDevices);
            Assert.Equal(20, analysis.Server.BusinessCatchupSeconds);
            Assert.Equal(300, analysis.Server.MaximumAckSendCompletedElapsedMilliseconds);
            Assert.Equal(40, analysis.Server.MaximumPendingCommandLine);
            Assert.Equal(30, analysis.Server.MaximumCommandLineQueueDepth);
            Assert.Equal(2, analysis.Server.AckSendAborted);
            Assert.Equal(3, analysis.Server.ConnectionClosed);
            Assert.Equal(3, analysis.SchemaVersion);
            Assert.Equal("TCPCenter-TEST-01", analysis.Background.ServerName);
            Assert.Equal("hyz_feat_对接电表", analysis.Background.CodeBranch);
            Assert.Equal("command-pipeline-workers", analysis.Background.ServiceParameters[0].Name);
            Assert.Equal("命令行覆盖标题", analysis.Title);
            Assert.Equal("cli-version", analysis.Background.ServiceVersion);
            Assert.True(analysis.AcceptancePassed);
            Assert.NotEmpty(analysis.Comparisons);
            Assert.True(File.Exists(Path.Combine(output, "acceptance-report.html")));
            Assert.True(File.Exists(Path.Combine(output, "diagnostic-report.md")));
            Assert.True(File.Exists(Path.Combine(output, "analysis.json")));
            Assert.True(File.Exists(Path.Combine(output, "phase-summary.csv")));
            Assert.True(File.Exists(Path.Combine(output, "charts", "online-timeline.svg")));
            var acceptanceHtml = await File.ReadAllTextAsync(Path.Combine(output, "acceptance-report.html"));
            var diagnosticHtml = await File.ReadAllTextAsync(Path.Combine(output, "diagnostic-report.html"));
            Assert.Contains("测试背景", acceptanceHtml, StringComparison.Ordinal);
            Assert.Contains("TCPCenter-TEST-01", acceptanceHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("问题分析", acceptanceHtml, StringComparison.Ordinal);
            Assert.Contains("问题分析", diagnosticHtml, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 验证背景 JSON 出现重复参数名时会在报告生成前返回稳定配置错误。
    /// </summary>
    [Fact]
    public async Task BackgroundWithDuplicateParameterNames_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"device-detailed-background-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "service": {
                    "parameters": [
                      { "name": "Worker", "value": "16" },
                      { "name": "worker", "value": "32" }
                    ]
                  }
                }
                """,
                new UTF8Encoding(false));

            var exception = await Assert.ThrowsAsync<ConfigurationValidationException>(
                () => DetailedReportBackgroundLoader.LoadAsync(path, CancellationToken.None));

            Assert.Contains(exception.Errors, item => item.Contains("参数名称", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// 验证缺少 TcpCenter 指标时服务端验收项明确标记为不可评估，并使综合验收失败。
    /// </summary>
    [Fact]
    public async Task MissingServerMetrics_AreUnavailableAndDoNotSilentlyPass()
    {
        var root = Path.Combine(Path.GetTempPath(), $"device-detailed-report-missing-{Guid.NewGuid():N}");
        try
        {
            var shard = Path.Combine(root, "shard-000");
            var startedAt = DateTimeOffset.Parse("2026-08-21T02:30:00Z");
            await NodeReportWriter.WriteAsync(CreateReport(startedAt, startedAt.AddMinutes(1)), shard, CancellationToken.None);
            await WriteClientSeriesAsync(shard, startedAt);

            var analysis = await DetailedReportGenerator.GenerateAsync(
                new DetailedReportGenerationOptions
                {
                    InputDirectory = shard,
                    OutputDirectory = Path.Combine(root, "analysis"),
                    Audience = DetailedReportAudience.Acceptance,
                    ServerTimeZoneId = "UTC",
                    TargetDeviceCount = 100
                },
                CancellationToken.None);

            Assert.Null(analysis.Server);
            Assert.False(analysis.AcceptancePassed);
            Assert.Contains(
                analysis.AcceptanceChecks,
                item => item.Name == "业务在线追平时间" && item.Status == DetailedAcceptanceStatus.Unavailable);
            Assert.Contains(analysis.DataWarnings, item => item.Contains("未找到 TcpCenter 服务指标文件", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 创建通过原始验收且目标为一百台的最小节点报告。
    /// </summary>
    /// <param name="startedAt">测试开始时间。</param>
    /// <param name="endedAt">测试结束时间。</param>
    /// <returns>可由正式节点报告写入器序列化的报告。</returns>
    private static NodeReport CreateReport(DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 100;
        options.Distributed.RunId = "detailed-report-test";
        var metrics = CreateMetrics(endedAt, 100);
        return new NodeReport
        {
            RunId = "detailed-report-test",
            NodeName = "node-a",
            ShardIndex = 0,
            ShardCount = 1,
            LocalDeviceCount = 100,
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt,
            Options = options,
            Metrics = metrics,
            Resources = new ResourceSnapshot(endedAt, 12, 100_000_000, 30_000_000, 1, 0, 0, 8, 0),
            Phases = [],
            RunStatus = LoadTestRunStatus.Passed,
            Acceptance = new AcceptanceResult(true, []),
            PreflightWarnings = []
        };
    }

    /// <summary>
    /// 创建给定在线数的客户端指标快照。
    /// </summary>
    /// <param name="timestamp">采样时间。</param>
    /// <param name="online">TCP和协议在线数。</param>
    /// <returns>客户端指标快照。</returns>
    private static MetricSnapshot CreateMetrics(DateTimeOffset timestamp, long online) => new()
    {
        TimestampUtc = timestamp,
        ExpectedOnline = online,
        TcpConnected = online,
        ProtocolOnline = online,
        ConnectionAttempts = online,
        ConnectionSucceeded = online,
        LoginAttempts = online,
        LoginSucceeded = online
    };

    /// <summary>
    /// 写入两个客户端时间点，使离线分析可以使用峰值而不依赖最终报告的 cooldown 语义。
    /// </summary>
    /// <param name="shard">分片目录。</param>
    /// <param name="startedAt">测试开始时间。</param>
    /// <returns>写入完成的异步任务。</returns>
    private static async Task WriteClientSeriesAsync(string shard, DateTimeOffset startedAt)
    {
        var serializerOptions = LoadTestConfigurationLoader.CreateSerializerOptions();
        serializerOptions.WriteIndented = false;
        var first = new TimeSeriesEntry(
            CreateMetrics(startedAt, 0),
            new ResourceSnapshot(startedAt, 1, 10_000_000, 5_000_000, 0, 0, 0, 2, 0),
            null);
        var second = new TimeSeriesEntry(
            CreateMetrics(startedAt.AddSeconds(10), 100),
            new ResourceSnapshot(startedAt.AddSeconds(10), 15, 110_000_000, 35_000_000, 1, 0, 0, 9, 1),
            null);
        var content = JsonSerializer.Serialize(first, serializerOptions) + Environment.NewLine +
            JsonSerializer.Serialize(second, serializerOptions) + Environment.NewLine;
        await File.WriteAllTextAsync(
            Path.Combine(shard, "node-timeseries.jsonl"),
            content,
            new UTF8Encoding(false));
    }

    /// <summary>
    /// 构造包含测试前基线、物理连接目标和业务在线目标三个采样点的 TcpCenter JSONL。
    /// </summary>
    /// <returns>服务端指标 JSONL。</returns>
    private static string BuildServerMetrics() => """
        {"Timestamp":"2026-08-21T02:29:00Z","ProcessId":10,"SocketConnections":0,"BoundDeviceConnections":0,"BusinessOnlineDevices":0,"BoundWithoutBusinessLine":0,"PendingLogin":0,"PendingHeartbeat":0,"PendingCommandLine":0,"PendingLifecycleState":0,"PendingConfirmSend":0,"AckMaxElapsedMs":0,"AckSendCompletedMaxElapsedMs":0,"LoginBusinessReadyMaxElapsedMs":0,"CommandLineStartPauseCount":0,"CommandLineStartMaxPauseElapsedMs":0,"AckedButRejected":{"Total":0},"StaleBusiness":{"Total":0},"HeartbeatCoalesced":{"Total":0},"AckFailed":{"Total":0},"AckSendAborted":{"Total":0},"ConnectionClosed":{"Total":0},"ConnectionCloseWithError":{"Total":0},"ConnectionCloseReceive":{"Total":0},"ConnectionCloseOther":{"Total":0},"CommandLineRejected":{"Total":0},"LifecycleStateRejected":{"Total":0},"Pipeline":{"WorkerCount":16,"QueueDepth":0,"Failed":0,"Rejected":0,"MaxQueueDepth":0,"MaxQueueAgeMs":0},"CommandLinePipeline":{"QueueDepth":0,"MaxQueueDepth":0,"MaxQueueAgeMs":0},"LifecyclePipeline":{"QueueDepth":0,"MaxQueueDepth":0,"MaxQueueAgeMs":0}}
        {"Timestamp":"2026-08-21T02:30:10Z","ProcessId":10,"SocketConnections":100,"BoundDeviceConnections":100,"BusinessOnlineDevices":50,"BoundWithoutBusinessLine":50,"PendingLogin":50,"PendingHeartbeat":0,"PendingCommandLine":40,"PendingLifecycleState":3,"PendingConfirmSend":5,"AckMaxElapsedMs":100,"AckSendCompletedMaxElapsedMs":300,"LoginBusinessReadyMaxElapsedMs":15000,"CommandLineStartPauseCount":0,"CommandLineStartMaxPauseElapsedMs":0,"AckedButRejected":{"Total":0},"StaleBusiness":{"Total":0},"HeartbeatCoalesced":{"Total":0},"AckFailed":{"Total":0},"AckSendAborted":{"Total":2},"ConnectionClosed":{"Total":3},"ConnectionCloseWithError":{"Total":1},"ConnectionCloseReceive":{"Total":1},"ConnectionCloseOther":{"Total":2},"CommandLineRejected":{"Total":0},"LifecycleStateRejected":{"Total":0},"Pipeline":{"WorkerCount":16,"QueueDepth":50,"Failed":0,"Rejected":0,"MaxQueueDepth":50,"MaxQueueAgeMs":1000},"CommandLinePipeline":{"QueueDepth":30,"MaxQueueDepth":30,"MaxQueueAgeMs":500},"LifecyclePipeline":{"QueueDepth":2,"MaxQueueDepth":2,"MaxQueueAgeMs":200}}
        {"Timestamp":"2026-08-21T02:30:30Z","ProcessId":10,"SocketConnections":100,"BoundDeviceConnections":100,"BusinessOnlineDevices":100,"BoundWithoutBusinessLine":0,"PendingLogin":0,"PendingHeartbeat":0,"PendingCommandLine":0,"PendingLifecycleState":0,"PendingConfirmSend":0,"AckMaxElapsedMs":200,"AckSendCompletedMaxElapsedMs":250,"LoginBusinessReadyMaxElapsedMs":12000,"CommandLineStartPauseCount":0,"CommandLineStartMaxPauseElapsedMs":0,"AckedButRejected":{"Total":0},"StaleBusiness":{"Total":0},"HeartbeatCoalesced":{"Total":0},"AckFailed":{"Total":0},"AckSendAborted":{"Total":2},"ConnectionClosed":{"Total":3},"ConnectionCloseWithError":{"Total":1},"ConnectionCloseReceive":{"Total":1},"ConnectionCloseOther":{"Total":2},"CommandLineRejected":{"Total":0},"LifecycleStateRejected":{"Total":0},"Pipeline":{"WorkerCount":16,"QueueDepth":0,"Failed":0,"Rejected":0,"MaxQueueDepth":50,"MaxQueueAgeMs":2000},"CommandLinePipeline":{"QueueDepth":0,"MaxQueueDepth":30,"MaxQueueAgeMs":500},"LifecyclePipeline":{"QueueDepth":0,"MaxQueueDepth":2,"MaxQueueAgeMs":200}}
        """;

    /// <summary>
    /// 构造包含服务器规格、代码版本、场景说明和关键参数的测试背景 JSON。
    /// </summary>
    /// <returns>可由正式背景加载器读取的 JSON。</returns>
    private static string BuildBackgroundJson() => """
        {
          "report": {
            "title": "背景配置报告",
            "environment": "公司测试环境",
            "tester": "自动化测试"
          },
          "scenario": {
            "name": "background-test",
            "description": "验证背景信息进入双受众报告"
          },
          "server": {
            "name": "TCPCenter-TEST-01",
            "operatingSystem": "Windows Server 2022",
            "cpu": "8 vCPU",
            "memory": "16 GB",
            "network": "1 Gbps"
          },
          "service": {
            "name": "Gather.TcpCenter",
            "version": "2026.08.21",
            "codeBranch": "hyz_feat_对接电表",
            "codeCommit": "1234567",
            "parameters": [
              { "name": "command-pipeline-workers", "value": "16" }
            ]
          }
        }
        """;
}
