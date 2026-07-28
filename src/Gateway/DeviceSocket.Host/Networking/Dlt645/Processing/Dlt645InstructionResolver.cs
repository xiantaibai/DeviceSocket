using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Host.Networking.Processing.Common;

namespace DeviceSocket.Host.Networking.Dlt645.Processing;

/// <summary>
/// 根据功能码、数据标识和应答方向生成 645 指令键。
/// </summary>
public sealed class Dlt645InstructionResolver
    : IProtocolInstructionResolver<Dlt645Frame, Dlt645InstructionKey>
{
    /// <inheritdoc />
    public Dlt645InstructionKey FallbackKey => Dlt645InstructionKey.Fallback;

    /// <inheritdoc />
    public Dlt645InstructionKey Resolve(Dlt645Frame frame) =>
        Dlt645InstructionKey.ForInstruction(
            frame.FunctionCode,
            frame.DataIdentifier,
            frame.IsResponse);
}
