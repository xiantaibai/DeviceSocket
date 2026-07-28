using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Core.Runtime.Metrics;
using DeviceSocket.Host.Networking.Processing.Common;

namespace DeviceSocket.Host.Networking.QGdw3761.Responses;

/// <summary>
/// 生成并发送 376.1 链路确认，同时隔离 Socket 和日志异常。
/// </summary>
public sealed class QGdw3761LinkConfirmationSender(
    RuntimeMetrics metrics,
    IRecentStore<DeviceErrorEvent> errors,
    IDeviceTrafficSink trafficSink,
    ILogger<QGdw3761LinkConfirmationSender> logger)
{
    /// <summary>
    /// 使用上行消息的 MsgId 发送标准链路确认，发送失败只记录错误。
    /// </summary>
    public async ValueTask SendAsync(ProtocolMessageContext<QGdw3761Frame> context)
    {
        var session = context.Session;
        var request = context.Frame;
        try
        {
            var response = QGdw3761Codec.CreateLinkConfirmation(request);
            await session.SendAsync(response);
            metrics.MessageSent();
            trafficSink.TryWrite(new DeviceTrafficRecord(
                DateTimeOffset.UtcNow,
                context.MsgId,
                MessageDirection.Downstream,
                request.Protocol,
                request.DeviceId,
                session.SessionID,
                response.Length,
                HexCodec.ToHex(response),
                Source: "LinkConfirmation"));
        }
        catch (Exception exception)
        {
            metrics.SendFailed();
            errors.Add(new DeviceErrorEvent(
                DateTimeOffset.UtcNow,
                "Send",
                "LINK_CONFIRMATION_FAILED",
                exception.Message,
                DeviceProtocol.QGdw3761,
                request.DeviceId,
                session.SessionID,
                session.RemoteEndPoint?.ToString(),
                TruncateHex(request.Raw)));
            SafeLog(() => logger.LogWarning(
                exception,
                "376.1 confirmation failed for {DeviceId} / {SessionId}",
                request.DeviceId,
                session.SessionID));
        }
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
            // 确认日志失败不能影响设备收包处理。
        }
    }
}
