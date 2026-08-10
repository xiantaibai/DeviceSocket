using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Networking.Common;
using DeviceSocket.Host.Networking.Processing.Common;
using DeviceSocket.Host.Networking.QGdw3761.Responses;

namespace DeviceSocket.Host.Networking.QGdw3761.Processing.Handlers;

/// <summary>
/// 处理 376.1 心跳，条件刷新设备路由租约并返回链路确认。
/// </summary>
public sealed class QGdw3761HeartbeatHandler(
    DeviceConnectionCoordinator connectionCoordinator,
    QGdw3761LinkConfirmationSender confirmationSender)
    : IProtocolInstructionHandler<QGdw3761Frame, QGdw3761InstructionKey>
{
    public QGdw3761InstructionKey Key =>
        QGdw3761InstructionKey.ForLink(QGdw3761LinkMessageKind.Heartbeat);

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        ProtocolMessageContext<QGdw3761Frame> context,
        CancellationToken cancellationToken)
    {
        if (context.Frame.DeviceId is null)
            return;

        if (!connectionCoordinator.Touch(
                context.Session.SessionID,
                context.Frame.DeviceId,
                context.ReceivedAt))
        {
            // 旧 Session 或未登录连接不能继续收到心跳确认，否则设备会误判失效链路仍然在线。
            await connectionCoordinator.CloseSessionAsync(
                context.Session.SessionID,
                DeviceSessionCloseReason.StaleSession);
            return;
        }

        await confirmationSender.SendAsync(context);
    }
}
