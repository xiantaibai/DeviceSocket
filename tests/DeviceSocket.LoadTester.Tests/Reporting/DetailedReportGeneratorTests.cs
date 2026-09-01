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
            Assert.Equal(5, analysis.SchemaVersion);
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
    /// 验证多分片阶段漂移时以最后一个分片进入冷却的时间截断服务端窗口，保留其他分片仍在恢复期间的队列压力。
    /// </summary>
    [Fact]
    public async Task StaggeredShardCooldown_UsesLatestCooldownStartForServerDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), $"device-detailed-staggered-{Guid.NewGuid():N}");
        try
        {
            var startedAt = DateTimeOffset.Parse("2026-08-21T02:30:00Z");
            var firstCooldownAt = startedAt.AddMinutes(1);
            var lastCooldownAt = startedAt.AddMinutes(2);
            var endedAt = startedAt.AddMinutes(3);
            for (var shardIndex = 0; shardIndex < 2; shardIndex++)
            {
                var shardPath = Path.Combine(root, $"shard-{shardIndex:D3}");
                var cooldownAt = shardIndex == 0 ? firstCooldownAt : lastCooldownAt;
                await NodeReportWriter.WriteAsync(
                    CreateDistributedReport(startedAt, endedAt, cooldownAt, shardIndex),
                    shardPath,
                    CancellationToken.None);
            }

            var serverPath = Path.Combine(root, "server.jsonl");
            await File.WriteAllTextAsync(
                serverPath,
                BuildStaggeredCooldownServerMetrics(),
                new UTF8Encoding(false));

            var analysis = await DetailedReportGenerator.GenerateAsync(
                new DetailedReportGenerationOptions
                {
                    InputDirectory = root,
                    ServerMetricsPath = serverPath,
                    OutputDirectory = Path.Combine(root, "analysis"),
                    Audience = DetailedReportAudience.Diagnostic,
                    ServerTimeZoneId = "UTC",
                    TargetDeviceCount = 100
                },
                CancellationToken.None);

            Assert.NotNull(analysis.Server);
            Assert.Equal(999, analysis.Server.MaximumQueueDepth);
            Assert.Equal(60_000, analysis.Server.MaximumQueueAgeMilliseconds);
            Assert.Contains(
                analysis.DataWarnings,
                item => item.Contains("全部分片进入 TCP cooldown", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 验证逐请求应答率优先于阶段累计差值，避免上一阶段迟到应答导致合并应答率超过百分之百。
    /// </summary>
    [Fact]
    public async Task DetailedTraceResponseRate_DoesNotExceedOneHundredPercent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"device-detailed-response-{Guid.NewGuid():N}");
        try
        {
            var shard = Path.Combine(root, "shard-000");
            var startedAt = DateTimeOffset.Parse("2026-08-21T02:30:00Z");
            var endedAt = startedAt.AddMinutes(1);
            var report = CreateReport(startedAt, endedAt) with
            {
                CommandPhases =
                [
                    new CommandPhaseResult
                    {
                        Name = "async-command",
                        StartedAtUtc = startedAt,
                        EndedAtUtc = endedAt,
                        LocalDeviceCount = 10,
                        PlannedRequests = 10,
                        ShowResult = false,
                        ApiRequestsStarted = 10,
                        ApiRequestsSucceeded = 10,
                        ApiRequestsFailed = 0,
                        ApiRequestTimeouts = 0,
                        ApiEstimatedEnqueued = 10,
                        GatherReadingCommandsReceived = 10,
                        GatherReadingCommandsResponded = 12,
                        RawDownstreamCommandsObserved = 10,
                        ProtocolRecognizedCommands = 10,
                        GatherReadingResponseRate = 0.9
                    }
                ]
            };
            await NodeReportWriter.WriteAsync(report, shard, CancellationToken.None);

            var analysis = await DetailedReportGenerator.GenerateAsync(
                new DetailedReportGenerationOptions
                {
                    InputDirectory = shard,
                    OutputDirectory = Path.Combine(root, "analysis"),
                    Audience = DetailedReportAudience.Diagnostic,
                    ServerTimeZoneId = "UTC",
                    TargetDeviceCount = 100
                },
                CancellationToken.None);

            var phase = Assert.Single(analysis.CommandPhases);
            Assert.Equal(9, phase.CommandsResponded);
            Assert.Equal(0.9, phase.ResponseRate, precision: 6);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 验证无计划重启场景中单台设备的亚秒级重连只标记为低优先级瞬时波动。
    /// </summary>
    [Fact]
    public async Task SingleDeviceSubsecondReconnect_IsReportedAsP2Transient()
    {
        var root = Path.Combine(Path.GetTempPath(), $"device-detailed-transient-{Guid.NewGuid():N}");
        try
        {
            var shard = Path.Combine(root, "shard-000");
            var startedAt = DateTimeOffset.Parse("2026-08-21T02:30:00Z");
            var endedAt = startedAt.AddMinutes(1);
            var outageStartedAt = startedAt.AddSeconds(30);
            var metrics = CreateMetrics(endedAt, 100) with
            {
                OutageCount = 1,
                ReconnectsScheduled = 1,
                WorstRecoveryMilliseconds = 248,
                Outages =
                [
                    new OutageEventSnapshot
                    {
                        Sequence = 1,
                        PhaseName = "steady",
                        PhaseKind = ScenarioPhaseKind.Steady,
                        ExpectedServerRestart = false,
                        StartedAtUtc = outageStartedAt,
                        RecoveredAtUtc = outageStartedAt.AddMilliseconds(248),
                        DurationMilliseconds = 248,
                        ExpectedOnline = 100,
                        MinimumProtocolOnline = 99,
                        MaximumOfflineDevices = 1,
                        ConnectionAttempts = 1,
                        ConnectionFailures = 0,
                        LoginFailures = 0,
                        HeartbeatTimeouts = 0,
                        ReconnectsScheduled = 1,
                        AddressAlreadyInUse = 0
                    }
                ]
            };
            var report = CreateReport(startedAt, endedAt) with { Metrics = metrics };
            await NodeReportWriter.WriteAsync(report, shard, CancellationToken.None);

            var analysis = await DetailedReportGenerator.GenerateAsync(
                new DetailedReportGenerationOptions
                {
                    InputDirectory = shard,
                    OutputDirectory = Path.Combine(root, "analysis"),
                    Audience = DetailedReportAudience.Diagnostic,
                    ServerTimeZoneId = "UTC",
                    TargetDeviceCount = 100
                },
                CancellationToken.None);

            var finding = Assert.Single(
                analysis.Findings,
                item => item.Title == "个别设备发生瞬时重连");
            Assert.Equal(DiagnosticSeverity.P2, finding.Severity);
            Assert.DoesNotContain(
                analysis.Findings,
                item => item.Title.Contains("计划重启后", StringComparison.Ordinal));
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
    /// 创建具有独立冷却开始时间的两分片节点报告，用于验证合并诊断窗口不会被先结束的节点提前截断。
    /// </summary>
    /// <param name="startedAt">整轮测试开始时间。</param>
    /// <param name="endedAt">当前分片结束时间。</param>
    /// <param name="cooldownAt">当前分片进入冷却阶段的时间。</param>
    /// <param name="shardIndex">当前零基分片编号。</param>
    /// <returns>包含稳态和冷却阶段的节点报告。</returns>
    private static NodeReport CreateDistributedReport(
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        DateTimeOffset cooldownAt,
        int shardIndex)
    {
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 100;
        options.Distributed.RunId = "staggered-cooldown-test";
        options.Distributed.ShardCount = 2;
        options.Distributed.ShardIndex = shardIndex;
        var metrics = CreateMetrics(endedAt, 50);
        return new NodeReport
        {
            RunId = options.Distributed.RunId,
            NodeName = $"node-{shardIndex}",
            ShardIndex = shardIndex,
            ShardCount = 2,
            LocalDeviceCount = 50,
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt,
            Options = options,
            Metrics = metrics,
            Resources = new ResourceSnapshot(endedAt, 10, 50_000_000, 20_000_000, 1, 0, 0, 4, 0),
            Phases =
            [
                new PhaseResult(
                    "steady",
                    ScenarioPhaseKind.Steady,
                    startedAt,
                    cooldownAt,
                    50,
                    metrics,
                    MinimumProtocolOnline: 50,
                    MaximumProtocolOnline: 50),
                new PhaseResult(
                    "cooldown",
                    ScenarioPhaseKind.Cooldown,
                    cooldownAt,
                    endedAt,
                    0,
                    metrics,
                    MinimumProtocolOnline: 0,
                    MaximumProtocolOnline: 50)
            ],
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
    /// 构造在第一个分片冷却后、最后一个分片冷却前出现 Pipeline 积压的服务端指标。
    /// </summary>
    /// <returns>用于验证多分片有效诊断窗口的服务端 JSONL。</returns>
    private static string BuildStaggeredCooldownServerMetrics() => """
        {"Timestamp":"2026-08-21T02:30:30Z","ProcessId":10,"SocketConnections":100,"BoundDeviceConnections":100,"BusinessOnlineDevices":100,"BoundWithoutBusinessLine":0,"PendingLogin":0,"PendingHeartbeat":0,"PendingCommandLine":0,"PendingLifecycleState":0,"PendingConfirmSend":0,"AckMaxElapsedMs":10,"AckSendCompletedMaxElapsedMs":10,"LoginBusinessReadyMaxElapsedMs":10,"CommandLineStartPauseCount":0,"CommandLineStartMaxPauseElapsedMs":0,"AckedButRejected":{"Total":0},"StaleBusiness":{"Total":0},"HeartbeatCoalesced":{"Total":0},"AckFailed":{"Total":0},"AckSendAborted":{"Total":0},"ConnectionClosed":{"Total":0},"ConnectionCloseWithError":{"Total":0},"ConnectionCloseReceive":{"Total":0},"ConnectionCloseOther":{"Total":0},"CommandLineRejected":{"Total":0},"LifecycleStateRejected":{"Total":0},"Pipeline":{"WorkerCount":16,"QueueDepth":0,"Failed":0,"Rejected":0,"MaxQueueDepth":0,"MaxQueueAgeMs":0},"CommandLinePipeline":{"QueueDepth":0,"MaxQueueDepth":0,"MaxQueueAgeMs":0},"LifecyclePipeline":{"QueueDepth":0,"MaxQueueDepth":0,"MaxQueueAgeMs":0}}
        {"Timestamp":"2026-08-21T02:31:30Z","ProcessId":10,"SocketConnections":50,"BoundDeviceConnections":50,"BusinessOnlineDevices":50,"BoundWithoutBusinessLine":0,"PendingLogin":0,"PendingHeartbeat":0,"PendingCommandLine":0,"PendingLifecycleState":0,"PendingConfirmSend":0,"AckMaxElapsedMs":10,"AckSendCompletedMaxElapsedMs":10,"LoginBusinessReadyMaxElapsedMs":10,"CommandLineStartPauseCount":0,"CommandLineStartMaxPauseElapsedMs":0,"AckedButRejected":{"Total":0},"StaleBusiness":{"Total":0},"HeartbeatCoalesced":{"Total":0},"AckFailed":{"Total":0},"AckSendAborted":{"Total":0},"ConnectionClosed":{"Total":50},"ConnectionCloseWithError":{"Total":0},"ConnectionCloseReceive":{"Total":0},"ConnectionCloseOther":{"Total":50},"CommandLineRejected":{"Total":0},"LifecycleStateRejected":{"Total":0},"Pipeline":{"WorkerCount":16,"QueueDepth":999,"Failed":0,"Rejected":0,"MaxQueueDepth":999,"MaxQueueAgeMs":60000},"CommandLinePipeline":{"QueueDepth":0,"MaxQueueDepth":0,"MaxQueueAgeMs":0},"LifecyclePipeline":{"QueueDepth":0,"MaxQueueDepth":0,"MaxQueueAgeMs":0}}
        {"Timestamp":"2026-08-21T02:32:30Z","ProcessId":10,"SocketConnections":0,"BoundDeviceConnections":0,"BusinessOnlineDevices":0,"BoundWithoutBusinessLine":0,"PendingLogin":0,"PendingHeartbeat":0,"PendingCommandLine":0,"PendingLifecycleState":0,"PendingConfirmSend":0,"AckMaxElapsedMs":0,"AckSendCompletedMaxElapsedMs":0,"LoginBusinessReadyMaxElapsedMs":0,"CommandLineStartPauseCount":0,"CommandLineStartMaxPauseElapsedMs":0,"AckedButRejected":{"Total":0},"StaleBusiness":{"Total":0},"HeartbeatCoalesced":{"Total":0},"AckFailed":{"Total":0},"AckSendAborted":{"Total":0},"ConnectionClosed":{"Total":100},"ConnectionCloseWithError":{"Total":0},"ConnectionCloseReceive":{"Total":0},"ConnectionCloseOther":{"Total":100},"CommandLineRejected":{"Total":0},"LifecycleStateRejected":{"Total":0},"Pipeline":{"WorkerCount":16,"QueueDepth":0,"Failed":0,"Rejected":0,"MaxQueueDepth":0,"MaxQueueAgeMs":0},"CommandLinePipeline":{"QueueDepth":0,"MaxQueueDepth":0,"MaxQueueAgeMs":0},"LifecyclePipeline":{"QueueDepth":0,"MaxQueueDepth":0,"MaxQueueAgeMs":0}}
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
