using System.Text.Json;
using DeviceSocket.Host.Configuration.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace DeviceSocket.Tests.Gateway;

/// <summary>
/// 验证程序日志 JSON 格式化器的固定字段和中文输出。
/// </summary>
public sealed class SysLogJsonFormatterTests
{
    /// <summary>
    /// 格式化后应生成一行可解析的 JSON，并保留关联字段和异常信息。
    /// </summary>
    [Fact]
    public void Format_ShouldWriteExpectedJsonFields()
    {
        var exception = new InvalidOperationException("模拟异常");
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 7, 28, 12, 34, 56, 789, TimeSpan.FromHours(8)),
            LogEventLevel.Warning,
            exception,
            new MessageTemplateParser().Parse("设备 {DeviceId} 处理失败"),
            [
                new LogEventProperty("DeviceId", new ScalarValue("3761:3301-00001")),
                new LogEventProperty("EventId", new ScalarValue(1102)),
                new LogEventProperty("EventName", new ScalarValue("ProtocolHandlerFailed")),
                new LogEventProperty("SourceContext", new ScalarValue("DeviceSocket.Test.Worker")),
                new LogEventProperty("LocalHost", new ScalarValue("gateway-01")),
                new LogEventProperty("Callsite", new ScalarValue("HandleAsync")),
                new LogEventProperty("TraceId", new ScalarValue("trace-001")),
                new LogEventProperty("Host", new ScalarValue("gateway-01")),
                new LogEventProperty("Application", new ScalarValue("DeviceSocket.Host"))
            ]);
        using var output = new StringWriter();

        new SysLogJsonFormatter().Format(logEvent, output);

        var line = output.ToString();
        Assert.EndsWith(Environment.NewLine, line, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Equal("2026-07-28 12:34:56.789", root.GetProperty("Longdate").GetString());
        Assert.Equal("WARNING", root.GetProperty("Level").GetString());
        Assert.Equal("DeviceSocket.Test.Worker", root.GetProperty("Logger").GetString());
        Assert.Equal("Application", root.GetProperty("LogType").GetString());
        Assert.Equal(1102, root.GetProperty("EventId").GetInt32());
        Assert.Equal("ProtocolHandlerFailed", root.GetProperty("EventName").GetString());
        Assert.Equal("gateway-01", root.GetProperty("LocalHost").GetString());
        Assert.Equal("HandleAsync", root.GetProperty("Callsite").GetString());
        Assert.Equal("trace-001", root.GetProperty("TraceId").GetString());
        Assert.Equal("gateway-01", root.GetProperty("Host").GetString());
        Assert.Equal("DeviceSocket.Host", root.GetProperty("Application").GetString());
        Assert.Equal("设备 \"3761:3301-00001\" 处理失败", root.GetProperty("Info").GetString());
        Assert.Contains("模拟异常", root.GetProperty("Exception").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            "3761:3301-00001",
            root.GetProperty("Properties").GetProperty("DeviceId").GetString());
    }
}
