using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Host.Networking.Processing.Common;

namespace DeviceSocket.Host.Networking.Dlt645.Processing.Handlers;

/// <summary>
/// 承接尚未配置业务 Handler 的 645 指令，保留设备绑定和完整审计日志。
/// </summary>
public sealed class Dlt645FallbackHandler
    : IProtocolInstructionHandler<Dlt645Frame, Dlt645InstructionKey>
{
    public Dlt645InstructionKey Key => Dlt645InstructionKey.Fallback;

    /// <inheritdoc />
    public ValueTask HandleAsync(
        ProtocolMessageContext<Dlt645Frame> context,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
