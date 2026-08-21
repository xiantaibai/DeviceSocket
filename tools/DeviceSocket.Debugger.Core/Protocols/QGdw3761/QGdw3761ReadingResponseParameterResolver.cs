using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;

namespace DeviceSocket.Debugger.Core.Protocols.QGdw3761;

/// <summary>
/// 保存从 376.1 AFN=0CH/F129 下行读表报文中解析出的抄读回复参数。
/// </summary>
/// <param name="AdministrativeDivision">4 位行政区划码。</param>
/// <param name="TerminalAddress">16 位终端地址。</param>
/// <param name="MasterStationAddress">主站地址 MSA。</param>
/// <param name="GroupAddress">A3 最低位群地址标志。</param>
/// <param name="Sequence">下行报文低 4 位帧序号，用于回复 RSEQ。</param>
/// <param name="MeasurementPoint">测量点 PN。</param>
public sealed record QGdw3761ReadingResponseParameters(
    string AdministrativeDivision,
    ushort TerminalAddress,
    byte MasterStationAddress,
    bool GroupAddress,
    byte Sequence,
    int MeasurementPoint);

/// <summary>
/// 解析主站下发的 AFN=0CH/F129 读表命令，并为抄读回复窗口生成可直接回填的参数。
/// </summary>
public static class QGdw3761ReadingResponseParameterResolver
{
    /// <summary>
    /// 把一段 376.1 下行 HEX 报文解析为抄读回复所需参数。
    /// </summary>
    /// <param name="hex">允许空格、短横线或冒号分隔的完整 376.1 帧。</param>
    /// <returns>用于回填地址、MSA、群地址、RSEQ 和 PN 的参数集合。</returns>
    /// <exception cref="FormatException">报文不是合法的 AFN=0CH/F129 下行读表命令，或包含多个测量点时抛出。</exception>
    public static QGdw3761ReadingResponseParameters Resolve(string? hex)
    {
        if (!HexCodec.TryParse(hex, out var bytes, out var error))
            throw new FormatException($"下发报文解析失败：{error}");

        var readResult = QGdw3761Codec.ReadCompleteFrame(bytes);
        if (readResult.Package is not QGdw3761Frame frame)
            throw new FormatException("下发报文不是有效的 376.1 帧。");
        if (!frame.IsValid)
            throw new FormatException($"下发报文校验失败：{frame.Error?.Message ?? "未知协议错误"}");
        if (frame.IsUpstream || !frame.IsPrimaryMessage)
            throw new FormatException("下发报文必须是主站发出的启动帧。");
        if (frame.Afn != 0x0C || frame.Functions.Count != 1 || frame.Functions[0] != 129)
            throw new FormatException("下发报文不是 AFN=0CH/F129 的读表命令。");
        if (frame.Points.Count != 1)
            throw new FormatException("下发报文必须只包含一个测量点，无法直接生成单点抄读回复。");

        return new QGdw3761ReadingResponseParameters(
            frame.AdministrativeDivision ?? string.Empty,
            checked((ushort)frame.TerminalAddress),
            frame.MasterStationAddress,
            frame.IsGroupAddress,
            frame.Sequence,
            frame.Points[0]);
    }
}
