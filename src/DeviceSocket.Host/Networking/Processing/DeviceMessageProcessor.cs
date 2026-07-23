using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Core.Runtime.Connections;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Core.Runtime.Metrics;
using DeviceSocket.Host.Networking.Common;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceSocket.Host.Networking.Processing;

/// <summary>
/// 负责连接生命周期、设备注册、上行消息记录、376.1 链路确认和异常隔离。
/// </summary>
public sealed class DeviceMessageProcessor(
    IOnlineDeviceRegistry registry,
    IRecentStore<ReceivedDeviceMessage> messages,
    IRecentStore<DeviceErrorEvent> errors,
    RuntimeMetrics metrics,
    ILogger<DeviceMessageProcessor> logger)
{
    /// <summary>
    /// 处理 TCP Session 建立事件并更新连接指标。
    /// </summary>
    public ValueTask SessionOpenedAsync(IAppSession session, DeviceProtocol protocol)
    {
        metrics.ConnectionOpened(protocol);
        SafeLog(() => logger.LogDebug("TCP session opened: {SessionId}, {Protocol}, {RemoteEndPoint}", session.SessionID, protocol, session.RemoteEndPoint));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 处理 Session 关闭事件，按 SessionId 清理设备映射并更新指标。
    /// </summary>
    public ValueTask SessionClosedAsync(IAppSession session, DeviceProtocol protocol, string? reason)
    {
        registry.RemoveSession(session.SessionID);
        metrics.ConnectionClosed(protocol);
        SafeLog(() => logger.LogDebug("TCP session closed: {SessionId}, {Protocol}, {Reason}", session.SessionID, protocol, reason));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 处理 376.1 上行帧；登录负责注册，心跳刷新在线时间，链路检测帧返回标准确认。
    /// </summary>
    public async ValueTask HandleQGdw3761Async(IAppSession session, QGdw3761Frame frame)
    {
        try
        {
            if (!frame.IsValid)
            {
                RecordProtocolError(session, frame);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            metrics.MessageReceived();
            messages.Add(ToReceivedMessage(session, frame, now));

            if (frame.DeviceId is null)
                return;

            // 只有明确的登录帧建立设备到 Session 的绑定，普通报文不能冒充登录。
            switch (frame.LinkMessageKind)
            {
                case QGdw3761LinkMessageKind.Login:
                    registry.Register(frame.DeviceId, new SuperSocketDeviceConnection(session, frame.Protocol), now);
                    await Send376ConfirmationAsync(session, frame);
                    break;

                case QGdw3761LinkMessageKind.Heartbeat:
                    registry.Touch(frame.DeviceId, session.SessionID, now);
                    await Send376ConfirmationAsync(session, frame);
                    break;

                case QGdw3761LinkMessageKind.Logout:
                    await Send376ConfirmationAsync(session, frame);
                    registry.RemoveSession(session.SessionID);
                    break;

                default:
                    registry.Touch(frame.DeviceId, session.SessionID, now);
                    break;
            }
        }
        catch (Exception exception)
        {
            RecordHandlerException(session, DeviceProtocol.QGdw3761, frame.DeviceId, exception);
        }
    }

    /// <summary>
    /// 处理 645 上行帧；协议没有网络登录命令，因此首个合法地址帧即建立绑定。
    /// </summary>
    public ValueTask HandleDlt645Async(IAppSession session, Dlt645Frame frame)
    {
        try
        {
            if (!frame.IsValid)
            {
                RecordProtocolError(session, frame);
                return ValueTask.CompletedTask;
            }

            var now = DateTimeOffset.UtcNow;
            metrics.MessageReceived();
            messages.Add(ToReceivedMessage(session, frame, now));
            if (frame.DeviceId is not null)
                registry.Register(frame.DeviceId, new SuperSocketDeviceConnection(session, frame.Protocol), now);
        }
        catch (Exception exception)
        {
            RecordHandlerException(session, DeviceProtocol.Dlt6452007, frame.DeviceId, exception);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 发送 376.1 链路确认；发送失败只记录错误，不把异常传播到收包调度器。
    /// </summary>
    private async ValueTask Send376ConfirmationAsync(IAppSession session, QGdw3761Frame request)
    {
        try
        {
            var response = QGdw3761Codec.CreateLinkConfirmation(request);
            await session.SendAsync(response);
            metrics.MessageSent();
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
            SafeLog(() => logger.LogWarning(exception, "376.1 confirmation failed for {DeviceId} / {SessionId}", request.DeviceId, session.SessionID));
        }
    }

    /// <summary>
    /// 将错误帧转换为结构化错误事件，并限制原始报文记录长度。
    /// </summary>
    private void RecordProtocolError(IAppSession session, IProtocolFrame frame)
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
        SafeLog(() => logger.LogWarning("Protocol error: {Protocol} {Code} {SessionId} {Message}", frame.Protocol, frame.Error?.Code, session.SessionID, frame.Error?.Message));
    }

    /// <summary>
    /// 记录业务处理器中的未预期异常，避免单条消息影响后续连接处理。
    /// </summary>
    private void RecordHandlerException(IAppSession session, DeviceProtocol protocol, string? deviceId, Exception exception)
    {
        errors.Add(new DeviceErrorEvent(
            DateTimeOffset.UtcNow,
            "Handler",
            "MESSAGE_HANDLER_FAILED",
            exception.Message,
            protocol,
            deviceId,
            session.SessionID,
            session.RemoteEndPoint?.ToString()));
        SafeLog(() => logger.LogError(exception, "Message handler failed for {Protocol} {DeviceId} {SessionId}", protocol, deviceId, session.SessionID));
    }

    private static ReceivedDeviceMessage ToReceivedMessage(IAppSession session, IProtocolFrame frame, DateTimeOffset now) => new(
        now,
        frame.Protocol,
        frame.DeviceId,
        session.SessionID,
        session.RemoteEndPoint?.ToString(),
        frame.Summary,
        HexCodec.ToHex(frame.Raw));

    private static string TruncateHex(byte[] bytes)
    {
        const int maximumBytes = 256;
        var hex = HexCodec.ToHex(bytes.AsSpan(0, Math.Min(bytes.Length, maximumBytes)));
        return bytes.Length > maximumBytes ? $"{hex}..." : hex;
    }

    /// <summary>
    /// 隔离日志提供程序自身的异常，确保记录日志不会反向打断协议处理。
    /// </summary>
    private static void SafeLog(Action writeLog)
    {
        try
        {
            writeLog();
        }
        catch
        {
            // 日志提供程序异常不能反向中断协议解析或 Session 生命周期处理。
        }
    }
}
