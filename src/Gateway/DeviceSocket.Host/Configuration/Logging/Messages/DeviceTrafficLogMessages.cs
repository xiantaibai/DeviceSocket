using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Messages;

namespace DeviceSocket.Host.Configuration.Logging.Messages;

/// <summary>
/// 提供设备上行和下发通讯事件的编译时源生成日志方法。
/// </summary>
internal static partial class DeviceTrafficLogMessages
{
    /// <summary>记录一条普通设备上行或链路管理报文。</summary>
    [LoggerMessage(
        EventId = 2001,
        EventName = "DeviceTrafficUpstream",
        Level = LogLevel.Information,
        Message = "Device upstream Direction={Direction} MsgId={MsgId} AttemptNo={AttemptNo} TraceId={TraceId} DeviceId={DeviceId} SessionId={SessionId} Protocol={Protocol} TrafficKind={TrafficKind} Source={Source} Bytes={Bytes} RawHex={RawHex} OccurredAt={OccurredAt}")]
    internal static partial void Upstream(
        ILogger logger,
        MessageDirection direction,
        string msgId,
        int? attemptNo,
        string? traceId,
        string? deviceId,
        string sessionId,
        DeviceProtocol protocol,
        ProtocolTrafficKind trafficKind,
        string? source,
        int bytes,
        string rawHex,
        DateTimeOffset occurredAt);

    /// <summary>记录一条按设备白名单或采样规则启用的上行心跳。</summary>
    [LoggerMessage(
        EventId = 2002,
        EventName = "DeviceHeartbeatUpstream",
        Level = LogLevel.Information,
        Message = "Device heartbeat upstream Direction={Direction} MsgId={MsgId} AttemptNo={AttemptNo} TraceId={TraceId} DeviceId={DeviceId} SessionId={SessionId} Protocol={Protocol} TrafficKind={TrafficKind} Source={Source} Bytes={Bytes} RawHex={RawHex} OccurredAt={OccurredAt}")]
    internal static partial void HeartbeatUpstream(
        ILogger logger,
        MessageDirection direction,
        string msgId,
        int? attemptNo,
        string? traceId,
        string? deviceId,
        string sessionId,
        DeviceProtocol protocol,
        ProtocolTrafficKind trafficKind,
        string? source,
        int bytes,
        string rawHex,
        DateTimeOffset occurredAt);

    /// <summary>记录一条普通设备下发或链路管理确认报文。</summary>
    [LoggerMessage(
        EventId = 2101,
        EventName = "DeviceTrafficDownstream",
        Level = LogLevel.Information,
        Message = "Device downstream Direction={Direction} MsgId={MsgId} AttemptNo={AttemptNo} TraceId={TraceId} DeviceId={DeviceId} SessionId={SessionId} Protocol={Protocol} TrafficKind={TrafficKind} Source={Source} Bytes={Bytes} RawHex={RawHex} OccurredAt={OccurredAt}")]
    internal static partial void Downstream(
        ILogger logger,
        MessageDirection direction,
        string msgId,
        int? attemptNo,
        string? traceId,
        string? deviceId,
        string sessionId,
        DeviceProtocol protocol,
        ProtocolTrafficKind trafficKind,
        string? source,
        int bytes,
        string rawHex,
        DateTimeOffset occurredAt);

    /// <summary>记录一条与已启用上行心跳成对的下行心跳确认。</summary>
    [LoggerMessage(
        EventId = 2102,
        EventName = "DeviceHeartbeatConfirmation",
        Level = LogLevel.Information,
        Message = "Device heartbeat confirmation Direction={Direction} MsgId={MsgId} AttemptNo={AttemptNo} TraceId={TraceId} DeviceId={DeviceId} SessionId={SessionId} Protocol={Protocol} TrafficKind={TrafficKind} Source={Source} Bytes={Bytes} RawHex={RawHex} OccurredAt={OccurredAt}")]
    internal static partial void HeartbeatConfirmation(
        ILogger logger,
        MessageDirection direction,
        string msgId,
        int? attemptNo,
        string? traceId,
        string? deviceId,
        string sessionId,
        DeviceProtocol protocol,
        ProtocolTrafficKind trafficKind,
        string? source,
        int bytes,
        string rawHex,
        DateTimeOffset occurredAt);
}
