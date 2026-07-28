using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Host.Observability;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace DeviceSocket.Tests.Gateway;

/// <summary>
/// 验证设备通信日志到 Serilog 结构化事件的字段映射。
/// </summary>
public sealed class SerilogDeviceTrafficSinkTests
{
    /// <summary>
    /// 上报和下发审计所需的关联字段应完整写入 Command 事件。
    /// </summary>
    [Fact]
    public void TryWrite_ShouldCreateStructuredCommandEvent()
    {
        var collector = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collector)
            .CreateLogger();
        var sink = new SerilogDeviceTrafficSink(logger);
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
            2));

        Assert.True(written);
        var logEvent = Assert.Single(collector.Events);
        Assert.Equal(LogEventLevel.Information, logEvent.Level);
        AssertProperty(logEvent, "LogType", "Command");
        AssertProperty(logEvent, "EventType", "DeviceCommand");
        AssertProperty(logEvent, "Direction", MessageDirection.Downstream);
        AssertProperty(logEvent, "MsgId", "msg-001");
        AssertProperty(logEvent, "AttemptNo", 2);
        AssertProperty(logEvent, "TraceId", "trace-001");
        AssertProperty(logEvent, "DeviceId", "3761:3301-00001");
        AssertProperty(logEvent, "SessionId", "session-001");
        AssertProperty(logEvent, "Protocol", DeviceProtocol.QGdw3761);
        AssertProperty(logEvent, "Source", "ReliableCommand");
        AssertProperty(logEvent, "Bytes", 18);
        AssertProperty(logEvent, "RawHex", "68020068C900000000000270000001003C16");
        AssertProperty(logEvent, "OccurredAt", occurredAt);
    }

    private static void AssertProperty(LogEvent logEvent, string name, object expected)
    {
        var scalar = Assert.IsType<ScalarValue>(logEvent.Properties[name]);
        Assert.Equal(expected, scalar.Value);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
