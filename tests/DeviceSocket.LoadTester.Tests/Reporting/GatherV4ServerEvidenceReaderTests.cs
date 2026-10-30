using System.Text.Json;
using DeviceSocket.LoadTester.Reporting.Detailed;

namespace DeviceSocket.LoadTester.Tests.Reporting;

/// <summary>验证 OTLP 有效时间窗、重叠文件去重与累计计数重启，不把历史累计量当成本轮完成量。</summary>
public sealed class GatherV4ServerEvidenceReaderTests
{
    /// <summary>各队列采样时间略有错开时，末值必须包含每个队列，不能用最后一条零样本掩盖其他队列积压。</summary>
    [Fact]
    public async Task GaugeLastValue_IncludesAllQueueDimensions()
    {
        var file = Path.Combine(Path.GetTempPath(), $"gatherv4-queues-{Guid.NewGuid():N}.json");
        var start = DateTimeOffset.Parse("2026-10-26T00:00:00Z");
        var time = start.AddSeconds(1).ToUnixTimeMilliseconds() * 1_000_000;
        try
        {
            await File.WriteAllTextAsync(file, $$$$"""
                {"resourceMetrics":[{"scopeMetrics":[{"metrics":[{"name":"gather.tcp.pipeline.pending","gauge":{"dataPoints":[
                  {"timeUnixNano":"{{{{time}}}}","asInt":"5","attributes":[{"key":"queue","value":{"stringValue":"manual"}}]},
                  {"timeUnixNano":"{{{{time + 1000000}}}}","asInt":"0","attributes":[{"key":"queue","value":{"stringValue":"normal"}}]}
                ]}}]}]}]}
                """);
            var result = await GatherV4ServerEvidenceReader.ReadAsync(file, start, start.AddMinutes(1), new List<string>(), CancellationToken.None);
            Assert.Equal(5, Assert.Single(result).Last);
        }
        finally { File.Delete(file); }
    }
    /// <summary>重复采样不重复相加，窗口外样本不参与计算，Counter 重置按新计数恢复。</summary>
    [Fact]
    public async Task OtlpEvidence_DeduplicatesAndHandlesCounterResets()
    {
        var file = Path.Combine(Path.GetTempPath(), $"gatherv4-evidence-{Guid.NewGuid():N}.jsonl");
        var start = DateTimeOffset.Parse("2026-10-26T00:00:00Z");
        try
        {
            var first = Payload(start.AddSeconds(1), 100);
            await File.WriteAllTextAsync(file, string.Join('\n', Payload(start.AddSeconds(-1), 999), first, first,
                Payload(start.AddSeconds(2), 110), Payload(start.AddSeconds(3), 3), "{broken"));
            var warnings = new List<string>();
            var result = await GatherV4ServerEvidenceReader.ReadAsync(file, start, start.AddMinutes(1), warnings, CancellationToken.None);
            var metric = Assert.Single(result);
            Assert.Equal(3, metric.Samples);
            Assert.Equal(13, metric.Increase);
            Assert.Equal(1, metric.CounterResets);
            Assert.Equal(3, metric.Last);
            Assert.Contains(warnings, item => item.Contains("非法 JSON", StringComparison.Ordinal));
        }
        finally { File.Delete(file); }
    }

    /// <summary>生成脱敏的标准 Collector OTLP JSON 累计指标。</summary>
    /// <param name="time">采样时间。</param>
    /// <param name="value">累计匹配数量。</param>
    /// <returns>单行 ExportMetricsServiceRequest JSON。</returns>
    private static string Payload(DateTimeOffset time, long value) => JsonSerializer.Serialize(new
    {
        resourceMetrics = new[] { new {
            resource = new { attributes = new[] { new { key = "service.instance.id", value = new { stringValue = "test-connect" } } } },
            scopeMetrics = new[] { new { metrics = new[] { new {
                name = "gather.tcp.response.matched",
                sum = new { aggregationTemporality = 2, isMonotonic = true, dataPoints = new[] { new {
                    timeUnixNano = (time.ToUnixTimeMilliseconds() * 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    asInt = value.ToString(System.Globalization.CultureInfo.InvariantCulture) } } }
            } } } }
        } }
    });
}
