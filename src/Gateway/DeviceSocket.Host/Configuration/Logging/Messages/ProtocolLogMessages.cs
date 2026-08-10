using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Host.Configuration.Logging.Messages;

/// <summary>
/// 提供协议校验、指令处理和链路确认异常的编译时源生成日志方法。
/// </summary>
internal static partial class ProtocolLogMessages
{
    /// <summary>记录一条未通过协议校验的设备帧。</summary>
    [LoggerMessage(1101, LogLevel.Warning, "Protocol error: {Protocol} {Code} {SessionId} {Message}", EventName = "ProtocolFrameRejected")]
    internal static partial void FrameRejected(
        ILogger logger,
        DeviceProtocol protocol,
        ProtocolErrorCode? code,
        string sessionId,
        string? message);

    /// <summary>记录协议指令 Handler 执行异常。</summary>
    [LoggerMessage(1102, LogLevel.Error, "Message handler failed for {Protocol} {DeviceId} {SessionId}", EventName = "ProtocolHandlerFailed")]
    internal static partial void HandlerFailed(
        ILogger logger,
        DeviceProtocol protocol,
        string? deviceId,
        string sessionId,
        Exception exception);

    /// <summary>记录 Q/GDW 376.1 链路确认发送异常。</summary>
    [LoggerMessage(1103, LogLevel.Warning, "376.1 confirmation failed for {DeviceId} / {SessionId}", EventName = "QGdw3761ConfirmationFailed")]
    internal static partial void LinkConfirmationFailed(
        ILogger logger,
        string? deviceId,
        string sessionId,
        Exception exception);
}
