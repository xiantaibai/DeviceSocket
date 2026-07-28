using DeviceSocket.Core.Runtime.Messages;
using Serilog;

namespace DeviceSocket.Host.Observability;

/// <summary>
/// 将设备上报和下发转换为 Serilog 结构化 Command 事件。
/// </summary>
public sealed class SerilogDeviceTrafficSink
    : IDeviceTrafficSink
{
    private readonly Serilog.ILogger _commandLogger;

    /// <summary>
    /// 创建带有 Command 分流属性的日志器，后续每条记录不再重复创建上下文。
    /// </summary>
    public SerilogDeviceTrafficSink(Serilog.ILogger logger)
    {
        _commandLogger = logger
            .ForContext<SerilogDeviceTrafficSink>()
            .ForContext("LogType", "Command")
            .ForContext("EventType", "DeviceCommand");
    }

    /// <summary>
    /// 非阻塞提交一条结构化通信记录；异步队列溢出由健康监控统一告警。
    /// </summary>
    public bool TryWrite(DeviceTrafficRecord record)
    {
        try
        {
            _commandLogger.Information(
                "DeviceCommand {Direction} MsgId={MsgId} AttemptNo={AttemptNo} TraceId={TraceId} DeviceId={DeviceId} SessionId={SessionId} Protocol={Protocol} Source={Source} Bytes={Bytes} RawHex={RawHex} OccurredAt={OccurredAt}",
                record.Direction,
                record.MsgId,
                record.AttemptNo,
                record.TraceId,
                record.DeviceId,
                record.SessionId,
                record.Protocol,
                record.Source,
                record.Bytes,
                record.RawHex,
                record.OccurredAt);
            return true;
        }
        catch
        {
            // 日志组件异常不能传播到 Socket 收发路径。
            return false;
        }
    }
}
