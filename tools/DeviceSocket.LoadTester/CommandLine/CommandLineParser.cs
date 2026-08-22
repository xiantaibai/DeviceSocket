using System.Globalization;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Reporting.Detailed;

namespace DeviceSocket.LoadTester.CommandLine;

/// <summary>
/// 将 <c>run</c>、<c>report merge</c> 和 <c>report generate</c> 命令行参数解析为强类型命令，并集中处理必填项、重复项和数值格式错误。
/// </summary>
internal static class CommandLineParser
{
    /// <summary>
    /// 解析运行、合并或离线详细报告命令；未知命令和缺失参数通过 <see cref="ArgumentException"/> 返回稳定失败语义。
    /// </summary>
    /// <param name="args">进程收到的原始命令行参数。</param>
    /// <returns>可由应用入口穷举分派的强类型命令。</returns>
    internal static LoadTesterCommand Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return new HelpCommand();

        if (string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
            return ParseRun(args.AsSpan(1));
        if (args.Length >= 2 &&
            string.Equals(args[0], "report", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "merge", StringComparison.OrdinalIgnoreCase))
        {
            return ParseMerge(args.AsSpan(2));
        }
        if (args.Length >= 2 &&
            string.Equals(args[0], "report", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "generate", StringComparison.OrdinalIgnoreCase))
        {
            return ParseGenerate(args.AsSpan(2));
        }

        throw new ArgumentException($"未知命令：{string.Join(' ', args)}");
    }

    /// <summary>
    /// 输出全部受支持命令及关键参数，便于发布后的单文件程序在没有额外文档时仍可自助使用。
    /// </summary>
    internal static void PrintHelp()
    {
        Console.WriteLine("DeviceSocket.LoadTester");
        Console.WriteLine();
        Console.WriteLine("执行负载测试：");
        Console.WriteLine("  run --config <file> [--run-id <id>] [--shard-index <n>] [--shard-count <n>]");
        Console.WriteLine("      [--start-at-utc <ISO-8601>] [--output <directory>]");
        Console.WriteLine();
        Console.WriteLine("合并多节点报告：");
        Console.WriteLine("  report merge --input <directory> --output <directory>");
        Console.WriteLine();
        Console.WriteLine("根据既有结果生成验收/排障详细报告：");
        Console.WriteLine("  report generate --input <directory> [--server-metrics <file>] [--background <file>] [--output <directory>]");
        Console.WriteLine("      [--audience acceptance|diagnostic|both] [--baseline-input <directory>]");
        Console.WriteLine("      [--baseline-server-metrics <file>] [--title <text>] [--environment <text>]");
        Console.WriteLine("      [--service-version <text>] [--tester <text>] [--server-time-zone <id>]");
        Console.WriteLine("      [--business-catchup-seconds <n>] [--queue-age-seconds <n>]");
        Console.WriteLine("      [--ack-milliseconds <n>] [--target-devices <n>]");
    }

    private static RunLoadTesterCommand ParseRun(ReadOnlySpan<string> args)
    {
        var values = ParseOptions(args);
        RejectUnknown(values, "--config", "--run-id", "--shard-index", "--shard-count", "--start-at-utc", "--output");
        var configurationPath = Required(values, "--config");
        var overrides = new RunCommandOverrides(
            Optional(values, "--run-id"),
            OptionalInt(values, "--shard-index"),
            OptionalInt(values, "--shard-count"),
            OptionalDateTimeOffset(values, "--start-at-utc"),
            Optional(values, "--output"));
        return new RunLoadTesterCommand(configurationPath, overrides);
    }

    private static MergeReportCommand ParseMerge(ReadOnlySpan<string> args)
    {
        var values = ParseOptions(args);
        RejectUnknown(values, "--input", "--output");
        return new MergeReportCommand(Required(values, "--input"), Required(values, "--output"));
    }

    /// <summary>
    /// 解析离线详细报告参数，并在启动文件读取前完成枚举、正整数和基线组合约束校验。
    /// </summary>
    /// <param name="args">位于 <c>report generate</c> 之后的参数。</param>
    /// <returns>包含完整离线报告选项的强类型命令。</returns>
    private static GenerateDetailedReportCommand ParseGenerate(ReadOnlySpan<string> args)
    {
        var values = ParseOptions(args);
        RejectUnknown(
            values,
            "--input",
            "--server-metrics",
            "--background",
            "--output",
            "--audience",
            "--baseline-input",
            "--baseline-server-metrics",
            "--title",
            "--environment",
            "--service-version",
            "--tester",
            "--server-time-zone",
            "--business-catchup-seconds",
            "--queue-age-seconds",
            "--ack-milliseconds",
            "--target-devices");

        var input = Required(values, "--input");
        var baselineInput = Optional(values, "--baseline-input");
        var baselineMetrics = Optional(values, "--baseline-server-metrics");
        if (baselineMetrics is not null && baselineInput is null)
            throw new ArgumentException("设置 --baseline-server-metrics 时必须同时设置 --baseline-input。");

        var options = new DetailedReportGenerationOptions
        {
            InputDirectory = input,
            ServerMetricsPath = Optional(values, "--server-metrics"),
            BackgroundPath = Optional(values, "--background"),
            OutputDirectory = Optional(values, "--output") ?? Path.Combine(input, "analysis"),
            Audience = ParseAudience(Optional(values, "--audience")),
            BaselineInputDirectory = baselineInput,
            BaselineServerMetricsPath = baselineMetrics,
            Title = Optional(values, "--title"),
            Environment = Optional(values, "--environment"),
            ServiceVersion = Optional(values, "--service-version"),
            Tester = Optional(values, "--tester"),
            ServerTimeZoneId = Optional(values, "--server-time-zone") ?? "Local",
            MaximumBusinessCatchupSeconds = OptionalPositiveInt(values, "--business-catchup-seconds") ?? 60,
            MaximumQueueAgeSeconds = OptionalPositiveInt(values, "--queue-age-seconds") ?? 30,
            MaximumAckMilliseconds = OptionalPositiveInt(values, "--ack-milliseconds") ?? 5000,
            TargetDeviceCount = OptionalPositiveInt(values, "--target-devices")
        };
        return new GenerateDetailedReportCommand(options);
    }

    private static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"参数 {args[index]} 缺少对应值。");
            if (!values.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException($"参数 {args[index]} 不能重复设置。");
        }

        return values;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"缺少必需参数 {name}。");

    private static string? Optional(IReadOnlyDictionary<string, string> values, string name) =>
        values.GetValueOrDefault(name);

    private static int? OptionalInt(IReadOnlyDictionary<string, string> values, string name)
    {
        var value = Optional(values, name);
        if (value is null)
            return null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"参数 {name} 必须是整数。");
    }

    /// <summary>
    /// 解析可选正整数；报告阈值和目标设备数不接受零或负数，避免生成恒定失败或无意义的验收规则。
    /// </summary>
    /// <param name="values">已完成键值对切分的参数字典。</param>
    /// <param name="name">待读取的参数名称。</param>
    /// <returns>参数缺失时为空，否则返回正整数。</returns>
    private static int? OptionalPositiveInt(IReadOnlyDictionary<string, string> values, string name)
    {
        var parsed = OptionalInt(values, name);
        return parsed switch
        {
            null => null,
            > 0 => parsed,
            _ => throw new ArgumentException($"参数 {name} 必须大于 0。")
        };
    }

    /// <summary>
    /// 将面向用户的受众文本转换为稳定枚举，参数缺失时默认同时生成两种报告。
    /// </summary>
    /// <param name="value">命令行中的受众文本。</param>
    /// <returns>详细报告受众枚举。</returns>
    private static DetailedReportAudience ParseAudience(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DetailedReportAudience.Both;
        return Enum.TryParse<DetailedReportAudience>(value, true, out var parsed)
            ? parsed
            : throw new ArgumentException("参数 --audience 必须是 acceptance、diagnostic 或 both。");
    }

    private static DateTimeOffset? OptionalDateTimeOffset(IReadOnlyDictionary<string, string> values, string name)
    {
        var value = Optional(values, name);
        if (value is null)
            return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : throw new ArgumentException($"参数 {name} 必须是 ISO-8601 时间。");
    }

    private static void RejectUnknown(IReadOnlyDictionary<string, string> values, params string[] allowed)
    {
        var unknown = values.Keys.FirstOrDefault(key => !allowed.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
            throw new ArgumentException($"未知参数：{unknown}");
    }
}
