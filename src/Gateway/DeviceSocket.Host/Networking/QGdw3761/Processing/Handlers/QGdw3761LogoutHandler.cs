using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Networking.Common;
using DeviceSocket.Host.Networking.Processing.Common;
using DeviceSocket.Host.Networking.QGdw3761.Responses;

namespace DeviceSocket.Host.Networking.QGdw3761.Processing.Handlers;

/// <summary>
/// 处理 376.1 退出登录，先返回链路确认再条件移除设备路由。
/// </summary>
public sealed class QGdw3761LogoutHandler(
    DeviceConnectionCoordinator connectionCoordinator,
    QGdw3761LinkConfirmationSender confirmationSender)
    : IProtocolInstructionHandler<QGdw3761Frame, QGdw3761InstructionKey>
{
    public QGdw3761InstructionKey Key =>
        QGdw3761InstructionKey.ForLink(QGdw3761LinkMessageKind.Logout);

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        ProtocolMessageContext<QGdw3761Frame> context,
        CancellationToken cancellationToken)
    {
        await confirmationSender.SendAsync(context);
        connectionCoordinator.Disconnect(context.Session.SessionID, context.ReceivedAt);
        await connectionCoordinator.CloseSessionAsync(
            context.Session.SessionID,
            DeviceSessionCloseReason.Logout);
    }
}
