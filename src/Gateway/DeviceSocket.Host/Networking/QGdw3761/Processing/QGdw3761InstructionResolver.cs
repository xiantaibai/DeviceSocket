using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Host.Networking.Processing.Common;

namespace DeviceSocket.Host.Networking.QGdw3761.Processing;

/// <summary>
/// 根据链路消息类型或 AFN/Fn 生成 376.1 指令路由键。
/// </summary>
public sealed class QGdw3761InstructionResolver
    : IProtocolInstructionResolver<QGdw3761Frame, QGdw3761InstructionKey>
{
    /// <inheritdoc />
    public QGdw3761InstructionKey FallbackKey => QGdw3761InstructionKey.Fallback;

    /// <inheritdoc />
    public QGdw3761InstructionKey Resolve(QGdw3761Frame frame)
    {
        if (frame.LinkMessageKind != QGdw3761LinkMessageKind.Other)
            return QGdw3761InstructionKey.ForLink(frame.LinkMessageKind);

        // 当前 Codec 将帧首个数据单元的 Fn 投影到 Functions，后续扩展多数据单元时可在此返回多个 Key。
        var function = frame.Functions.Count > 0 ? frame.Functions[0] : 0;
        return QGdw3761InstructionKey.ForApplication(frame.Afn, function);
    }
}
