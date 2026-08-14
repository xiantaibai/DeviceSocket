using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Host.Networking.Cluster;
using DeviceSocket.Host.Networking.QGdw3761.Commands;

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

    /// <summary>记录迟到、重复、错配或无关联的 376.1 设备命令确认。</summary>
    [LoggerMessage(1104, LogLevel.Debug, "376.1 命令回复已忽略: {DeviceId} {SessionId} PSEQ={Sequence} {MatchStatus}", EventName = "QGdw3761CommandReplyIgnored")]
    internal static partial void CommandReplyIgnored(
        ILogger logger,
        string? deviceId,
        string sessionId,
        byte sequence,
        QGdw3761CommandReplyMatchStatus matchStatus);

    /// <summary>记录一条 376.1 命令回复的 Orleans 确定性处理结果，不包含完整原始报文。</summary>
    [LoggerMessage(1105, LogLevel.Debug, "376.1 命令回复已处理: {MsgId} attempt={AttemptNo} {DeviceId} ERR={ErrorCode} {Outcome} {Code}", EventName = "QGdw3761CommandReplyPublished")]
    internal static partial void CommandReplyPublished(
        ILogger logger,
        string msgId,
        int attemptNo,
        string deviceId,
        byte errorCode,
        DeviceCommandReplyPublishOutcome outcome,
        string code);

    /// <summary>记录 376.1 命令回复提交 Orleans 失败，关联将保留以等待重试。</summary>
    [LoggerMessage(1106, LogLevel.Warning, "376.1 命令回复提交 Orleans 失败: {MsgId} attempt={AttemptNo} {DeviceId}", EventName = "QGdw3761CommandReplyPublishFailed")]
    internal static partial void CommandReplyPublishFailed(
        ILogger logger,
        string msgId,
        int attemptNo,
        string deviceId,
        Exception exception);
}
