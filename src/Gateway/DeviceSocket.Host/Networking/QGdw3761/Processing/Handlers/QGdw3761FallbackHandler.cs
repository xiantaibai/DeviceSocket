using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Networking.Processing.Common;

namespace DeviceSocket.Host.Networking.QGdw3761.Processing.Handlers;

/// <summary>
/// 承接尚未配置业务 Handler 的 376.1 普通上报，保留连接和完整审计日志。
/// </summary>
public sealed class QGdw3761FallbackHandler
    : IProtocolInstructionHandler<QGdw3761Frame, QGdw3761InstructionKey>
{
    public QGdw3761InstructionKey Key => QGdw3761InstructionKey.Fallback;

    /// <inheritdoc />
    public ValueTask HandleAsync(
        ProtocolMessageContext<QGdw3761Frame> context,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
