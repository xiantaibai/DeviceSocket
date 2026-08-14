using DeviceSocket.Cluster.Contracts.Devices;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Configuration.Logging.Messages;
using DeviceSocket.Host.Networking.Cluster;
using DeviceSocket.Host.Networking.Processing.Common;
using DeviceSocket.Host.Networking.QGdw3761.Commands;

namespace DeviceSocket.Host.Networking.QGdw3761.Processing.Handlers;

/// <summary>
/// 处理终端 AFN=00H/F3 逐数据单元确认，并把通过 Session、PSEQ、AFN、DA、DT 校验的回复提交给 Orleans。
/// </summary>
public sealed class QGdw3761CommandReplyHandler(
    QGdw3761CommandCorrelationRegistry correlations,
    IDeviceCommandReplyPublisher replyPublisher,
    ILogger<QGdw3761CommandReplyHandler> logger)
    : IProtocolInstructionHandler<QGdw3761Frame, QGdw3761InstructionKey>
{
    /// <inheritdoc />
    public QGdw3761InstructionKey Key => QGdw3761InstructionKey.ForApplication(0x00, 3);

    /// <summary>
    /// 匹配并提交设备确认；迟到、重复、错配和无关联报文只记录分类，不调用 Orleans。
    /// </summary>
    /// <param name="context">已通过 376.1 校验和检查的上行消息上下文。</param>
    /// <param name="cancellationToken">当前协议处理取消令牌。</param>
    /// <returns>回复发布或丢弃完成后的异步结果。</returns>
    public async ValueTask HandleAsync(
        ProtocolMessageContext<QGdw3761Frame> context,
        CancellationToken cancellationToken)
    {
        var matchStatus = correlations.TryAcquireReply(
            context.Session.SessionID,
            context.Frame,
            context.ReceivedAt,
            out var match);
        if (matchStatus != QGdw3761CommandReplyMatchStatus.Matched || match is null)
        {
            ProtocolLogMessages.CommandReplyIgnored(
                logger,
                context.Frame.DeviceId,
                context.Session.SessionID,
                context.Frame.Sequence,
                matchStatus);
            return;
        }

        try
        {
            var result = await replyPublisher.PublishAsync(new DeviceCommandReply
            {
                DeviceId = match.DeviceId,
                MsgId = match.MsgId,
                AttemptNo = match.AttemptNo,
                SessionEpoch = match.SessionEpoch,
                Success = match.Success,
                Message = match.Success
                    ? "设备返回 376.1 AFN=00H/F3 成功确认。"
                    : $"设备返回 376.1 AFN=00H/F3 失败确认，ERR=0x{match.ErrorCode:X2}。",
                Payload = context.Frame.Raw.ToArray(),
                ReceivedAt = context.ReceivedAt,
                TraceId = match.TraceId
            });

            switch (result.Outcome)
            {
                case DeviceCommandReplyPublishOutcome.Completed:
                case DeviceCommandReplyPublishOutcome.Rejected:
                    correlations.CompleteReply(match);
                    break;
                case DeviceCommandReplyPublishOutcome.RetryScheduled:
                    correlations.SuspendReply(match);
                    break;
                default:
                    correlations.ReleaseReply(match);
                    break;
            }

            ProtocolLogMessages.CommandReplyPublished(
                logger,
                match.MsgId,
                match.AttemptNo,
                match.DeviceId,
                match.ErrorCode,
                result.Outcome,
                result.Code);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Orleans 调用失败不消费终端确认；释放发布权后，相同响应重发或下一次正式重试仍可完成逻辑命令。
            correlations.ReleaseReply(match);
            ProtocolLogMessages.CommandReplyPublishFailed(
                logger,
                match.MsgId,
                match.AttemptNo,
                match.DeviceId,
                exception);
        }
    }
}
