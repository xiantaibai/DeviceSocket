using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Host.Networking.Processing.Common;

namespace DeviceSocket.Host.Networking.Dlt645.Processing;

/// <summary>
/// 利用首个合法地址帧维护 645 设备绑定，再分发给匹配的统一 Handler。
/// </summary>
public sealed class Dlt645MessageDispatcher(
    DeviceConnectionCoordinator connectionCoordinator,
    ProtocolInstructionDispatcher<Dlt645Frame, Dlt645InstructionKey> instructionDispatcher)
    : IProtocolMessageDispatcher<Dlt645Frame>
{
    /// <inheritdoc />
    public async ValueTask DispatchAsync(
        ProtocolMessageContext<Dlt645Frame> context,
        CancellationToken cancellationToken)
    {
        if (context.Frame.DeviceId is not null)
        {
            connectionCoordinator.RegisterOrRefresh(
                context.Session,
                context.Frame.Protocol,
                context.Frame.DeviceId,
                context.ReceivedAt);
        }

        await instructionDispatcher.DispatchAsync(context, cancellationToken);
    }
}
