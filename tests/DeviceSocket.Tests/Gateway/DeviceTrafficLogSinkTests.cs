using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Host.Configuration.Logging;
using DeviceSocket.Host.Observability;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace DeviceSocket.Tests.Gateway;

/// <summary>
/// 验证设备通讯日志通过标准 ILogger 和源生成方法映射到 Serilog 结构化事件。
/// </summary>
public sealed class DeviceTrafficLogSinkTests
{
    /// <summary>
    /// 下发审计所需关联字段、稳定 Category 和 EventId 应完整写入结构化事件。
    /// </summary>
    [Fact]
    public void TryWrite_ShouldCreateStructuredDownstreamEvent()
    {
        var collector = new CollectingSink();
        using var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collector)
            .CreateLogger();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSerilog(serilogLogger, dispose: false));
        var metrics = new DeviceTrafficLoggingMetrics();
        var sink = new DeviceTrafficLogSink(loggerFactory, metrics);
        var occurredAt = new DateTimeOffset(2026, 7, 28, 10, 20, 30, TimeSpan.FromHours(8));

        var written = sink.TryWrite(new DeviceTrafficRecord(
            occurredAt,
            "msg-001",
            MessageDirection.Downstream,
            DeviceProtocol.QGdw3761,
            "3761:3301-00001",
            "session-001",
            18,
            "68020068C900000000000270000001003C16",
            "trace-001",
            "ReliableCommand",
            2,
            ProtocolTrafficKind.Business));

        Assert.True(written);
        Assert.Equal(1, metrics.DownstreamSubmitted);
        Assert.Equal(0, metrics.SynchronousWriteFailures);
        var logEvent = Assert.Single(collector.Events);
        Assert.Equal(LogEventLevel.Information, logEvent.Level);
        AssertProperty(logEvent, "SourceContext", DeviceTrafficLogCategories.DownstreamCategory);
        AssertEventId(logEvent, 2101, "DeviceTrafficDownstream");
        AssertProperty(logEvent, "Direction", MessageDirection.Downstream);
        AssertProperty(logEvent, "MsgId", "msg-001");
        AssertProperty(logEvent, "AttemptNo", 2);
        AssertProperty(logEvent, "TraceId", "trace-001");
        AssertProperty(logEvent, "DeviceId", "3761:3301-00001");
        AssertProperty(logEvent, "SessionId", "session-001");
        AssertProperty(logEvent, "Protocol", DeviceProtocol.QGdw3761);
        AssertProperty(logEvent, "TrafficKind", ProtocolTrafficKind.Business);
        AssertProperty(logEvent, "Source", "ReliableCommand");
        AssertProperty(logEvent, "Bytes", 18);
        AssertProperty(logEvent, "RawHex", "68020068C900000000000270000001003C16");
        AssertProperty(logEvent, "OccurredAt", occurredAt);
    }

    /// <summary>
    /// 断言指定 Serilog 属性以标量形式保存且值保持不变。
    /// </summary>
    /// <param name="logEvent">待检查的日志事件。</param>
    /// <param name="name">属性名称。</param>
    /// <param name="expected">期望值。</param>
    private static void AssertProperty(LogEvent logEvent, string name, object expected)
    {
        var scalar = Assert.IsType<ScalarValue>(logEvent.Properties[name]);
        Assert.Equal(expected, scalar.Value);
    }

    /// <summary>
    /// 断言 Microsoft 日志适配器生成的结构化 EventId 和 EventName。
    /// </summary>
    /// <param name="logEvent">待检查的日志事件。</param>
    /// <param name="expectedId">期望事件编号。</param>
    /// <param name="expectedName">期望事件名称。</param>
    private static void AssertEventId(LogEvent logEvent, int expectedId, string expectedName)
    {
        var eventId = Assert.IsType<StructureValue>(logEvent.Properties["EventId"]);
        var properties = eventId.Properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        Assert.Equal(expectedId, Assert.IsType<ScalarValue>(properties["Id"].Value).Value);
        Assert.Equal(expectedName, Assert.IsType<ScalarValue>(properties["Name"].Value).Value);
    }

    /// <summary>收集测试期间产生的 Serilog 事件。</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        /// <summary>已经接收的全部日志事件。</summary>
        public List<LogEvent> Events { get; } = [];

        /// <summary>保存一条日志事件。</summary>
        /// <param name="logEvent">Serilog 结构化事件。</param>
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
