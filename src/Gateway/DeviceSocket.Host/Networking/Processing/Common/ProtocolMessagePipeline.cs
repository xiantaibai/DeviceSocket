using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Core.Runtime.Metrics;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 为所有协议提供校验、指标、流量日志和异常隔离的通用收包管道。
/// </summary>
public sealed class ProtocolMessagePipeline<TFrame>(
    IProtocolMessageDispatcher<TFrame> dispatcher,
    IRecentStore<ReceivedDeviceMessage> messages,
    IRecentStore<DeviceErrorEvent> errors,
    RuntimeMetrics metrics,
    IDeviceTrafficSink trafficSink,
    ILogger<ProtocolMessagePipeline<TFrame>> logger)
    where TFrame : IProtocolFrame
{
    /// <summary>
    /// 记录一条完整上行帧后进入协议专属分发，任何单条帧异常均不向收包循环传播。
    /// </summary>
    public async ValueTask HandleAsync(IAppSession session, TFrame frame)
    {
        try
        {
            if (!frame.IsValid)
            {
                RecordProtocolError(session, frame);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var msgId = Guid.NewGuid().ToString("N");
            metrics.MessageReceived();
            messages.Add(new ReceivedDeviceMessage(
                msgId,
                MessageDirection.Upstream,
                now,
                frame.Protocol,
                frame.DeviceId,
                session.SessionID,
                session.RemoteEndPoint?.ToString(),
                frame.Summary,
                HexCodec.ToHex(frame.Raw)));
            trafficSink.TryWrite(new DeviceTrafficRecord(
                now,
                msgId,
                MessageDirection.Upstream,
                frame.Protocol,
                frame.DeviceId,
                session.SessionID,
                frame.Raw.Length,
                HexCodec.ToHex(frame.Raw),
                Source: "DeviceUpstream"));

            await dispatcher.DispatchAsync(
                new ProtocolMessageContext<TFrame>(session, frame, msgId, now),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            RecordHandlerException(session, frame, exception);
        }
    }

    private void RecordProtocolError(IAppSession session, TFrame frame)
    {
        metrics.ProtocolError();
        errors.Add(new DeviceErrorEvent(
            DateTimeOffset.UtcNow,
            "Protocol",
            frame.Error?.Code.ToString() ?? "UNKNOWN_PROTOCOL_ERROR",
            frame.Error?.Message ?? "未知协议错误。",
            frame.Protocol,
            frame.DeviceId,
            session.SessionID,
            session.RemoteEndPoint?.ToString(),
            TruncateHex(frame.Raw)));
        SafeLog(() => logger.LogWarning(
            "Protocol error: {Protocol} {Code} {SessionId} {Message}",
            frame.Protocol,
            frame.Error?.Code,
            session.SessionID,
            frame.Error?.Message));
    }

    private void RecordHandlerException(IAppSession session, TFrame frame, Exception exception)
    {
        errors.Add(new DeviceErrorEvent(
            DateTimeOffset.UtcNow,
            "Handler",
            "MESSAGE_HANDLER_FAILED",
            exception.Message,
            frame.Protocol,
            frame.DeviceId,
            session.SessionID,
            session.RemoteEndPoint?.ToString(),
            TruncateHex(frame.Raw)));
        SafeLog(() => logger.LogError(
            exception,
            "Message handler failed for {Protocol} {DeviceId} {SessionId}",
            frame.Protocol,
            frame.DeviceId,
            session.SessionID));
    }

    private static string TruncateHex(byte[] bytes)
    {
        const int maximumBytes = 256;
        var hex = HexCodec.ToHex(bytes.AsSpan(0, Math.Min(bytes.Length, maximumBytes)));
        return bytes.Length > maximumBytes ? $"{hex}..." : hex;
    }

    private static void SafeLog(Action writeLog)
    {
        try
        {
            writeLog();
        }
        catch
        {
            // 日志提供程序异常不能反向中断协议消息处理。
        }
    }
}
