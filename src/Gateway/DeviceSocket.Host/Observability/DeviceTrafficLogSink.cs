using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Host.Configuration.Logging;
using DeviceSocket.Host.Configuration.Logging.Messages;

namespace DeviceSocket.Host.Observability;

/// <summary>
/// 使用源生成日志方法把设备上报和下发提交到独立的 Serilog 异步文件流。
/// </summary>
public sealed class DeviceTrafficLogSink : IDeviceTrafficSink
{
    private readonly ILogger _upstreamLogger;
    private readonly ILogger _downstreamLogger;
    private readonly DeviceTrafficLoggingMetrics _metrics;

    /// <summary>
    /// 创建并缓存固定 Category Logger，避免每条通讯日志创建 Scope 或属性字典。
    /// </summary>
    /// <param name="loggerFactory">由 Serilog 提供程序承接的标准日志工厂。</param>
    /// <param name="metrics">通讯日志提交指标。</param>
    public DeviceTrafficLogSink(
        ILoggerFactory loggerFactory,
        DeviceTrafficLoggingMetrics metrics)
    {
        _upstreamLogger = loggerFactory.CreateLogger(DeviceTrafficLogCategories.UpstreamCategory);
        _downstreamLogger = loggerFactory.CreateLogger(DeviceTrafficLogCategories.DownstreamCategory);
        _metrics = metrics;
    }

    /// <summary>
    /// 非阻塞提交一条结构化通讯记录；同步日志异常被隔离并通过指标暴露。
    /// </summary>
    /// <param name="record">已经完成日志策略判断和 RawHex 转换的通讯记录。</param>
    /// <returns>成功提交给日志提供程序时返回 true；同步失败时返回 false。</returns>
    public bool TryWrite(DeviceTrafficRecord record)
    {
        try
        {
            if (record.Direction == MessageDirection.Upstream)
                WriteUpstream(record);
            else
                WriteDownstream(record);

            _metrics.TrafficSubmitted(record.Direction);
            return true;
        }
        catch
        {
            // 日志组件异常不能传播到 Socket 收发路径；异步队列后续丢弃由独立监控器统计。
            _metrics.SynchronousWriteFailed();
            return false;
        }
    }

    /// <summary>
    /// 按上行报文类型调用固定的源生成日志方法，避免热路径中的模板解析和装箱开销。
    /// </summary>
    /// <param name="record">待写入的设备上行记录。</param>
    private void WriteUpstream(DeviceTrafficRecord record)
    {
        if (record.TrafficKind == ProtocolTrafficKind.Heartbeat)
        {
            DeviceTrafficLogMessages.HeartbeatUpstream(
                _upstreamLogger,
                record.Direction,
                record.MsgId,
                record.AttemptNo,
                record.TraceId,
                record.DeviceId,
                record.SessionId,
                record.Protocol,
                record.TrafficKind,
                record.Source,
                record.Bytes,
                record.RawHex,
                record.OccurredAt);
            return;
        }

        DeviceTrafficLogMessages.Upstream(
            _upstreamLogger,
            record.Direction,
            record.MsgId,
            record.AttemptNo,
            record.TraceId,
            record.DeviceId,
            record.SessionId,
            record.Protocol,
            record.TrafficKind,
            record.Source,
            record.Bytes,
            record.RawHex,
            record.OccurredAt);
    }

    /// <summary>
    /// 按下行报文类型调用固定的源生成日志方法，使心跳确认能够按独立 EventId 检索。
    /// </summary>
    /// <param name="record">待写入的设备下行记录。</param>
    private void WriteDownstream(DeviceTrafficRecord record)
    {
        if (record.TrafficKind == ProtocolTrafficKind.HeartbeatConfirmation)
        {
            DeviceTrafficLogMessages.HeartbeatConfirmation(
                _downstreamLogger,
                record.Direction,
                record.MsgId,
                record.AttemptNo,
                record.TraceId,
                record.DeviceId,
                record.SessionId,
                record.Protocol,
                record.TrafficKind,
                record.Source,
                record.Bytes,
                record.RawHex,
                record.OccurredAt);
            return;
        }

        DeviceTrafficLogMessages.Downstream(
            _downstreamLogger,
            record.Direction,
            record.MsgId,
            record.AttemptNo,
            record.TraceId,
            record.DeviceId,
            record.SessionId,
            record.Protocol,
            record.TrafficKind,
            record.Source,
            record.Bytes,
            record.RawHex,
            record.OccurredAt);
    }
}
