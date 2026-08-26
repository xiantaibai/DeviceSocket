using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Reporting;

namespace DeviceSocket.LoadTester.Tests.Reporting;

/// <summary>
/// 验证节点报告会同时落盘聚合错误和可追踪的逐请求 HTTP 失败明细。
/// </summary>
public sealed class NodeReportWriterTests
{
    /// <summary>
    /// 验证 HTTP 失败明细 CSV 和 JSONL 包含阶段、设备、业务码及预校验分类。
    /// </summary>
    [Fact]
    public async Task HttpErrorDetails_AreWrittenToCsvAndJsonLines()
    {
        var output = Path.Combine(Path.GetTempPath(), $"loadtester-http-errors-{Guid.NewGuid():N}");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var options = TestOptionsFactory.Create();
            var report = new NodeReport
            {
                RunId = "http-errors",
                NodeName = "node-a",
                ShardIndex = 0,
                ShardCount = 1,
                LocalDeviceCount = 1,
                StartedAtUtc = now.AddSeconds(-1),
                EndedAtUtc = now,
                Options = options,
                Metrics = new MetricSnapshot { TimestampUtc = now },
                Resources = new ResourceSnapshot(now, 0, 0, 0, 0, 0, 0, 0, 0),
                Phases = [],
                Acceptance = new AcceptanceResult(false, []),
                PreflightWarnings = [],
                RunStatus = LoadTestRunStatus.Failed,
                HttpRequestErrors =
                [
                    new HttpRequestErrorDetail
                    {
                        TimestampUtc = now,
                        PhaseName = "sync,smoke",
                        MeterId = 7001,
                        DeviceId = "2608100001",
                        ShowResult = true,
                        RequestSequence = 1,
                        HttpStatusCode = 200,
                        ErrorCode = "API_BUSINESS_407",
                        ErrorMessage = "设备已停用",
                        ElapsedMilliseconds = 12,
                        TimedOut = false,
                        PrevalidationRejected = true,
                        ShardIndex = 0
                    }
                ],
                CommandTraces =
                [
                    new CommandTraceDetail
                    {
                        RequestId = 1,
                        PhaseName = "sync,smoke",
                        RequestSequence = 1,
                        MeterId = 7001,
                        DeviceId = "2608100001",
                        ShowResult = true,
                        RegisteredAtUtc = now.AddMilliseconds(-20),
                        HttpCompletedAtUtc = now,
                        HttpStatusCode = 200,
                        ApiSucceeded = true,
                        FirstDownstreamAtUtc = now.AddMilliseconds(-10),
                        DownstreamKind = DownstreamClassificationKind.GatherReadingF129,
                        AssociationConfidence = CommandAssociationConfidence.Ordered,
                        ResponseSentAtUtc = now.AddMilliseconds(-5),
                        ResponseSent = true,
                        Outcome = CommandTraceOutcome.Responded
                    }
                ],
                DownstreamDiagnostics = new DownstreamDiagnosticSummary
                {
                    RegisteredCommands = 1,
                    CommandsWithDownstream = 1,
                    RecognizedCommands = 1,
                    RespondedCommands = 1,
                    Classifications = new Dictionary<DownstreamClassificationKind, long>
                    {
                        [DownstreamClassificationKind.GatherReadingF129] = 1
                    }
                },
                DownstreamDiagnosticSamples =
                [
                    new DownstreamDiagnosticSample
                    {
                        TimestampUtc = now,
                        PhaseName = "sync,smoke",
                        RequestSequence = 1,
                        MeterId = 7001,
                        DeviceId = "2608100001",
                        Kind = DownstreamClassificationKind.GatherReadingMultiFunction,
                        AddressMatched = true,
                        AfnHex = "0C",
                        Functions = [129, 130],
                        RawLength = 20,
                        RawHex = "6801",
                        Fingerprint = "sample",
                        Reason = "混合功能单元"
                    }
                ]
            };

            await NodeReportWriter.WriteAsync(report, output, CancellationToken.None);

            var csv = await File.ReadAllTextAsync(Path.Combine(output, "http-errors.csv"));
            Assert.Contains("\"sync,smoke\"", csv, StringComparison.Ordinal);
            Assert.Contains("API_BUSINESS_407", csv, StringComparison.Ordinal);
            var jsonLine = await File.ReadAllTextAsync(Path.Combine(output, "http-errors.jsonl"));
            Assert.Single(jsonLine.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            var parsed = JsonSerializer.Deserialize<HttpRequestErrorDetail>(
                jsonLine,
                LoadTestConfigurationLoader.CreateSerializerOptions());
            Assert.NotNull(parsed);
            Assert.True(parsed.PrevalidationRejected);
            Assert.Equal(7001, parsed.MeterId);
            var commandTrace = await File.ReadAllTextAsync(Path.Combine(output, "command-traces.jsonl"));
            Assert.Contains("Responded", commandTrace, StringComparison.Ordinal);
            var targets = await File.ReadAllTextAsync(Path.Combine(output, "phase-targets.csv"));
            Assert.Contains("\"sync,smoke\"", targets, StringComparison.Ordinal);
            var classification = await File.ReadAllTextAsync(Path.Combine(output, "downstream-classification.csv"));
            Assert.Contains("GatherReadingF129", classification, StringComparison.Ordinal);
            var samples = await File.ReadAllTextAsync(Path.Combine(output, "downstream-unknown-samples.jsonl"));
            Assert.Contains("GatherReadingMultiFunction", samples, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }
}
