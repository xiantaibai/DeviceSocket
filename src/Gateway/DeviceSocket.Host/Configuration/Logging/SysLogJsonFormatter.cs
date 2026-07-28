using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;

namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// 将程序运行日志格式化为便于日志平台采集的单行 JSON。
/// </summary>
public sealed class SysLogJsonFormatter : ITextFormatter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 将一条 Serilog 事件输出为一个完整 JSON 对象，并以换行符结束。
    /// </summary>
    /// <param name="logEvent">待格式化的日志事件。</param>
    /// <param name="output">接收 JSON 文本的输出流。</param>
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        var logObject = new Dictionary<string, object?>
        {
            ["Longdate"] = logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            ["Level"] = logEvent.Level.ToString().ToUpperInvariant(),
            ["Logger"] = GetScalarProperty(logEvent, "SourceContext"),
            ["LocalHost"] = GetScalarProperty(logEvent, "LocalHost"),
            ["Callsite"] = GetScalarProperty(logEvent, "Callsite"),
            ["TraceId"] = GetScalarProperty(logEvent, "TraceId"),
            ["Host"] = GetScalarProperty(logEvent, "Host"),
            ["Application"] = GetScalarProperty(logEvent, "Application"),
            ["Info"] = logEvent.RenderMessage(),
            ["Exception"] = logEvent.Exception?.ToString()
        };

        var json = JsonSerializer.Serialize(logObject, SerializerOptions);
        output.WriteLine(json);
    }

    private static object? GetScalarProperty(LogEvent logEvent, string propertyName)
    {
        if (!logEvent.Properties.TryGetValue(propertyName, out var propertyValue))
            return null;

        // ScalarValue 直接保留原始类型，避免数字和布尔值被无条件转换成字符串。
        return propertyValue is ScalarValue scalarValue
            ? scalarValue.Value
            : propertyValue.ToString().Trim('"');
    }
}
