using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Networking.Processing.Common;

namespace DeviceSocket.Host.Networking.QGdw3761.Processing;

/// <summary>
/// 完成 376.1 普通报文活动续租，并将帧分发给匹配的统一 Handler。
/// </summary>
public sealed class QGdw3761MessageDispatcher(
    DeviceConnectionCoordinator connectionCoordinator,
    ProtocolInstructionDispatcher<QGdw3761Frame, QGdw3761InstructionKey> instructionDispatcher)
    : IProtocolMessageDispatcher<QGdw3761Frame>
{
    /// <inheritdoc />
    public async ValueTask DispatchAsync(
        ProtocolMessageContext<QGdw3761Frame> context,
        CancellationToken cancellationToken)
    {
        var frame = context.Frame;
        if (frame.LinkMessageKind == QGdw3761LinkMessageKind.Other && frame.DeviceId is not null)
            connectionCoordinator.Touch(context.Session.SessionID, frame.DeviceId, context.ReceivedAt);

        await instructionDispatcher.DispatchAsync(context, cancellationToken);
    }
}
