using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Core.Runtime.Metrics;
using DeviceSocket.Host.Configuration.Logging.Messages;
using DeviceSocket.Host.Networking.Common;
using DeviceSocket.Host.Observability;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 为所有协议提供校验、指标、流量日志和异常隔离的通用收包管道。
/// </summary>
/// <typeparam name="TFrame">当前协议解析后的帧类型。</typeparam>
/// <param name="dispatcher">当前协议的强类型消息分发器。</param>
/// <param name="messages">保存最近上行消息的有界内存存储。</param>
/// <param name="errors">保存最近协议和处理异常的有界内存存储。</param>
/// <param name="metrics">Gateway 运行指标。</param>
/// <param name="sessionManager">维护合法活动时间的 TCP Session 管理器。</param>
/// <param name="trafficSink">非阻塞设备通信日志入口。</param>
/// <param name="trafficLogPolicy">在生成 RawHex 前执行的通讯日志策略。</param>
/// <param name="logger">记录协议和 Handler 异常的程序日志器。</param>
public sealed class ProtocolMessagePipeline<TFrame>(
    IProtocolMessageDispatcher<TFrame> dispatcher,
    IRecentStore<ReceivedDeviceMessage> messages,
    IRecentStore<DeviceErrorEvent> errors,
    RuntimeMetrics metrics,
    DeviceTcpSessionManager sessionManager,
    IDeviceTrafficSink trafficSink,
    DeviceTrafficLogPolicy trafficLogPolicy,
    ILogger<ProtocolMessagePipeline<TFrame>> logger)
    where TFrame : IProtocolFrame
{
    /// <summary>
    /// 记录一条完整上行帧后进入协议专属分发，任何单条帧异常均不向收包循环传播。
    /// </summary>
    /// <param name="session">收到当前帧的 SuperSocket Session。</param>
    /// <param name="frame">已经由 PipelineFilter 完成边界识别和协议校验的帧。</param>
    /// <returns>消息记录和协议分发完成后的异步结果。</returns>
    public async ValueTask HandleAsync(IAppSession session, TFrame frame)
    {
        try
        {
            if (!frame.IsValid)
            {
                RecordProtocolError(session, frame);
                await sessionManager.RecordProtocolErrorAsync(session.SessionID);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            // 僵尸连接超时只认可完成协议校验的报文，垃圾字节不能无限延长 Session 生命周期。
            sessionManager.RecordValidMessage(session.SessionID, now);
            var msgId = Guid.NewGuid().ToString("N");
            metrics.MessageReceived();
            if (frame.TrafficKind == ProtocolTrafficKind.Heartbeat)
                metrics.HeartbeatReceived();

            var recordTraffic = trafficLogPolicy.ShouldRecord(frame.TrafficKind, frame.DeviceId);
            if (recordTraffic)
            {
                // 心跳策略在此之前已经完成；未命中心跳不生成 RawHex，也不会覆盖最近业务消息。
                var rawHex = HexCodec.ToHex(frame.Raw);
                messages.Add(new ReceivedDeviceMessage(
                    msgId,
                    MessageDirection.Upstream,
                    now,
                    frame.Protocol,
                    frame.DeviceId,
                    session.SessionID,
                    session.RemoteEndPoint?.ToString(),
                    frame.Summary,
                    rawHex));
                trafficSink.TryWrite(new DeviceTrafficRecord(
                    now,
                    msgId,
                    MessageDirection.Upstream,
                    frame.Protocol,
                    frame.DeviceId,
                    session.SessionID,
                    frame.Raw.Length,
                    rawHex,
                    Source: "DeviceUpstream",
                    TrafficKind: frame.TrafficKind));
            }

            await dispatcher.DispatchAsync(
                new ProtocolMessageContext<TFrame>(session, frame, msgId, now, recordTraffic),
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
        try
        {
            ProtocolLogMessages.FrameRejected(
                logger,
                frame.Protocol,
                frame.Error?.Code,
                session.SessionID,
                frame.Error?.Message);
        }
        catch
        {
            // 日志提供程序异常不能反向中断协议错误隔离。
        }
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
        try
        {
            ProtocolLogMessages.HandlerFailed(
                logger,
                frame.Protocol,
                frame.DeviceId,
                session.SessionID,
                exception);
        }
        catch
        {
            // 日志提供程序异常不能覆盖原始 Handler 异常记录。
        }
    }

    private static string TruncateHex(byte[] bytes)
    {
        const int maximumBytes = 256;
        var hex = HexCodec.ToHex(bytes.AsSpan(0, Math.Min(bytes.Length, maximumBytes)));
        return bytes.Length > maximumBytes ? $"{hex}..." : hex;
    }

}
