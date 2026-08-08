using DeviceSocket.LoadTester.CommandLine;

namespace DeviceSocket.LoadTester.Tests.CommandLine;

/// <summary>
/// 验证运行和多节点报告命令的参数失败语义。
/// </summary>
public sealed class CommandLineParserTests
{
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
}
