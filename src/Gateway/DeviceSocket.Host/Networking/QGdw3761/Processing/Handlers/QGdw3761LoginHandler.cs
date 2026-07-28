using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Networking.Processing.Common;
using DeviceSocket.Host.Networking.QGdw3761.Responses;

namespace DeviceSocket.Host.Networking.QGdw3761.Processing.Handlers;

/// <summary>
/// 处理 376.1 登录，建立设备 Session 绑定并返回链路确认。
/// </summary>
public sealed class QGdw3761LoginHandler(
    DeviceConnectionCoordinator connectionCoordinator,
    QGdw3761LinkConfirmationSender confirmationSender)
    : IProtocolInstructionHandler<QGdw3761Frame, QGdw3761InstructionKey>
{
    public QGdw3761InstructionKey Key =>
        QGdw3761InstructionKey.ForLink(QGdw3761LinkMessageKind.Login);

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        ProtocolMessageContext<QGdw3761Frame> context,
        CancellationToken cancellationToken)
    {
        if (context.Frame.DeviceId is null)
            return;

        connectionCoordinator.Register(
            context.Session,
            context.Frame.Protocol,
            context.Frame.DeviceId,
            context.ReceivedAt);
        await confirmationSender.SendAsync(context);
    }
}
