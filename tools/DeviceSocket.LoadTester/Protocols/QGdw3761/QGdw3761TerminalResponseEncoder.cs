using DeviceSocket.Core.Protocols.QGdw3761;

namespace DeviceSocket.LoadTester.Protocols.QGdw3761;

/// <summary>
/// 为虚拟 376.1 终端生成与生产 Gateway 约定一致的命令确认报文。
/// </summary>
internal static class QGdw3761TerminalResponseEncoder
{
    /// <summary>
    /// 按 Gateway 实际关联规则生成 AFN=00H/F3 终端确认，使压测终端与生产协议实现共用同一编码逻辑。
    /// </summary>
    /// <param name="request">已解析的主站下行启动帧。</param>
    /// <returns>完整终端确认报文。</returns>
    internal static byte[] CreateConfirmation(QGdw3761Frame request) =>
        QGdw3761Codec.CreateTerminalConfirmation(request);
}
