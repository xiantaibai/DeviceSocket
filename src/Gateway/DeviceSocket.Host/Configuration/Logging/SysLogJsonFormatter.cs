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
            ["LogType"] = GetScalarProperty(logEvent, "LogType") ?? LogBufferMonitorRegistry.ApplicationLogType,
            ["EventId"] = GetEventIdProperty(logEvent, "Id") ?? GetScalarProperty(logEvent, "EventId"),
            ["EventName"] = GetEventIdProperty(logEvent, "Name") ?? GetScalarProperty(logEvent, "EventName"),
            ["TraceId"] = GetScalarProperty(logEvent, "TraceId"),
            ["SpanId"] = GetScalarProperty(logEvent, "SpanId"),
            ["MsgId"] = GetScalarProperty(logEvent, "MsgId"),
            ["Host"] = GetScalarProperty(logEvent, "Host"),
            ["Application"] = GetScalarProperty(logEvent, "Application"),
            ["Info"] = logEvent.RenderMessage(),
            ["Exception"] = logEvent.Exception?.ToString(),
            ["Properties"] = GetStructuredProperties(logEvent)
        };

        var json = JsonSerializer.Serialize(logObject, SerializerOptions);
        output.WriteLine(json);
    }

    /// <summary>
    /// 读取一个 Serilog 标量属性，并保留数字和布尔值等原始类型。
    /// </summary>
    /// <param name="logEvent">待格式化的日志事件。</param>
    /// <param name="propertyName">属性名称。</param>
    /// <returns>属性值；不存在时返回 null。</returns>
    private static object? GetScalarProperty(LogEvent logEvent, string propertyName)
    {
        if (!logEvent.Properties.TryGetValue(propertyName, out var propertyValue))
            return null;

        // ScalarValue 直接保留原始类型，避免数字和布尔值被无条件转换成字符串。
        return propertyValue is ScalarValue scalarValue
            ? scalarValue.Value
            : propertyValue.ToString().Trim('"');
    }

    /// <summary>
    /// 读取 Microsoft ILogger 经 Serilog Provider 写入的结构化 EventId 子字段。
    /// </summary>
    /// <param name="logEvent">待格式化的日志事件。</param>
    /// <param name="propertyName">EventId 结构中的 Id 或 Name 字段。</param>
    /// <returns>字段值；当前事件没有结构化 EventId 时为空。</returns>
    private static object? GetEventIdProperty(LogEvent logEvent, string propertyName)
    {
        if (!logEvent.Properties.TryGetValue("EventId", out var eventId) ||
            eventId is not StructureValue structure)
        {
            return null;
        }

        return structure.Properties
            .Where(property => string.Equals(property.Name, propertyName, StringComparison.Ordinal))
            .Select(property => ConvertPropertyValue(property.Value))
            .FirstOrDefault();
    }

    /// <summary>
    /// 把 Serilog 属性树转换为 System.Text.Json 可直接序列化的普通对象，保留源生成日志参数。
    /// </summary>
    /// <param name="logEvent">待格式化的日志事件。</param>
    /// <returns>全部结构化属性组成的只读字典。</returns>
    private static IReadOnlyDictionary<string, object?> GetStructuredProperties(LogEvent logEvent) =>
        logEvent.Properties.ToDictionary(
            static property => property.Key,
            static property => ConvertPropertyValue(property.Value),
            StringComparer.Ordinal);

    /// <summary>
    /// 递归展开 Serilog 标量、序列、结构和字典属性，避免把复杂属性退化成不可查询文本。
    /// </summary>
    /// <param name="value">Serilog 属性值。</param>
    /// <returns>适合 JSON 序列化的普通 CLR 值。</returns>
    private static object? ConvertPropertyValue(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: Enum enumValue } => enumValue.ToString(),
        ScalarValue scalar => scalar.Value,
        SequenceValue sequence => sequence.Elements.Select(ConvertPropertyValue).ToArray(),
        StructureValue structure => structure.Properties.ToDictionary(
            static property => property.Name,
            static property => ConvertPropertyValue(property.Value),
            StringComparer.Ordinal),
        DictionaryValue dictionary => dictionary.Elements.ToDictionary(
            static property => property.Key.Value?.ToString() ?? string.Empty,
            static property => ConvertPropertyValue(property.Value),
            StringComparer.Ordinal),
        _ => value.ToString()
    };
}
