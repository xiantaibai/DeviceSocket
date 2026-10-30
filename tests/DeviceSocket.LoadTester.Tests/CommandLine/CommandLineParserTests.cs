using DeviceSocket.LoadTester.CommandLine;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Reporting.Detailed;

namespace DeviceSocket.LoadTester.Tests.CommandLine;

/// <summary>
/// 验证运行和多节点报告命令的参数失败语义。
/// </summary>
public sealed class CommandLineParserTests
{
    /// <summary>同一正式场景可切换为纯设备待命，保留自动应答而不调用 HTTP。</summary>
    [Fact]
    public void RunCommand_ParsesPassiveDeviceRole()
    {
        var command = Assert.IsType<RunLoadTesterCommand>(CommandLineParser.Parse(
            ["run", "--config", "manual.json", "--role", "devicesOnly"]));
        Assert.Equal(LoadTestRole.DevicesOnly, command.Overrides.Role);
    }
    /// <summary>
    /// 验证特殊运行编号不会生成可逃逸报告根目录的相对路径片段。
    /// </summary>
    [Theory]
    [InlineData("..", "_")]
    [InlineData("../outside", ".._outside")]
    [InlineData("中文-run.01", "中文-run.01")]
    public void RunId_IsSanitizedAsSinglePathSegment(string input, string expected)
    {
        Assert.Equal(expected, LoadTesterApplication.SanitizeRunId(input));
    }

    /// <summary>
    /// 验证运行命令能够解析分片和统一启动参数。
    /// </summary>
    [Fact]
    public void RunCommand_ParsesDistributedOverrides()
    {
        var command = Assert.IsType<RunLoadTesterCommand>(CommandLineParser.Parse(
        [
            "run", "--config", "scenario.json", "--run-id", "run-1",
            "--shard-index", "1", "--shard-count", "3",
            "--start-at-utc", "2026-08-07T12:00:00Z"
        ]));

        Assert.Equal("scenario.json", command.ConfigurationPath);
        Assert.Equal(1, command.Overrides.ShardIndex);
        Assert.Equal(3, command.Overrides.ShardCount);
        Assert.Equal(TimeSpan.Zero, command.Overrides.StartAtUtc!.Value.Offset);
    }

    /// <summary>
    /// 验证拼写错误的参数不会被静默忽略。
    /// </summary>
    [Fact]
    public void UnknownOption_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => CommandLineParser.Parse(
            ["run", "--config", "scenario.json", "--shrad-count", "3"]));
    }

    /// <summary>
    /// 验证离线详细报告命令能够解析受众、服务指标、基线和数值门槛。
    /// </summary>
    [Fact]
    public void DetailedReportCommand_ParsesAudienceAndThresholds()
    {
        var command = Assert.IsType<GenerateDetailedReportCommand>(CommandLineParser.Parse(
        [
            "report", "generate", "--input", "result", "--server-metrics", "server.jsonl",
            "--background", "background.json",
            "--audience", "diagnostic", "--baseline-input", "before",
            "--business-catchup-seconds", "45", "--queue-age-seconds", "20",
            "--ack-milliseconds", "3000", "--target-devices", "10000"
        ]));

        Assert.Equal(DetailedReportAudience.Diagnostic, command.Options.Audience);
        Assert.Equal("server.jsonl", command.Options.ServerMetricsPath);
        Assert.Equal("background.json", command.Options.BackgroundPath);
        Assert.Equal("before", command.Options.BaselineInputDirectory);
        Assert.Equal(45, command.Options.MaximumBusinessCatchupSeconds);
        Assert.Equal(20, command.Options.MaximumQueueAgeSeconds);
        Assert.Equal(3000, command.Options.MaximumAckMilliseconds);
        Assert.Equal(10000, command.Options.TargetDeviceCount);
    }

    /// <summary>
    /// 验证只设置基线服务指标而没有基线结果目录时会被拒绝，避免生成来源不匹配的比较报告。
    /// </summary>
    [Fact]
    public void DetailedReportCommand_RejectsOrphanBaselineMetrics()
    {
        Assert.Throws<ArgumentException>(() => CommandLineParser.Parse(
        [
            "report", "generate", "--input", "result",
            "--baseline-server-metrics", "before.jsonl"
        ]));
    }
}
