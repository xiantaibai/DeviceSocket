using System.Globalization;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.CommandLine;

/// <summary>
/// 将 <c>run</c> 和 <c>report merge</c> 命令行参数解析为强类型命令，并集中处理必填项、重复项和数值格式错误。
/// </summary>
internal static class CommandLineParser
{
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

        throw new ArgumentException($"未知命令：{string.Join(' ', args)}");
    }

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
