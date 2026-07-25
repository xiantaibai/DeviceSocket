using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;

namespace DeviceSocket.Debugger.Core.Decoding;

/// <summary>
/// 把协议帧转换为适合调试界面展示的中文字段说明。
/// </summary>
public static class FrameDisplayFormatter
{
    /// <summary>
    /// 格式化协议帧；无效帧会保留错误码、原因和原始数据。
    /// </summary>
    /// <param name="frame">待展示的协议帧。</param>
    /// <returns>多行字段说明。</returns>
    public static string Format(IProtocolFrame frame)
    {
        if (!frame.IsValid)
            return $"解析失败：{frame.Error?.Code}\n原因：{frame.Error?.Message}";

        return frame switch
        {
            QGdw3761Frame value => FormatQGdw3761(value),
            Dlt645Frame value => FormatDlt645(value),
            _ => frame.Summary
        };
    }

    private static string FormatQGdw3761(QGdw3761Frame frame) =>
        string.Join(
            Environment.NewLine,
            $"协议：Q/GDW 376.1-2012",
            $"设备：{frame.DeviceId}",
            $"方向：{(frame.IsUpstream ? "上行" : "下行")}",
            $"控制域：{frame.Control:X2}H",
            $"用户数据长度：{frame.UserDataLength}",
            $"行政区划：{frame.AdministrativeDivision}",
            $"终端地址：{frame.TerminalAddress}",
            $"AFN：{frame.Afn:X2}H",
            $"SEQ：{frame.Sequence}",
            $"信息点：{string.Join(", ", frame.Points)}",
            $"功能项：{string.Join(", ", frame.Functions.Select(value => $"F{value}"))}",
            $"链路类型：{frame.LinkMessageKind}",
            $"数据单元：{HexCodec.ToHex(frame.DataUnitPayload, true)}");

    private static string FormatDlt645(Dlt645Frame frame) =>
        string.Join(
            Environment.NewLine,
            "协议：DL/T 645-2007",
            $"设备：{frame.DeviceId}",
            $"方向：{(frame.IsResponse ? "应答/上行" : "请求/下行")}",
            $"地址：{frame.Address}",
            $"控制码：{frame.Control:X2}H",
            $"功能码：{frame.FunctionCode:X2}H",
            $"异常应答：{(frame.IsAbnormalResponse ? "是" : "否")}",
            $"后续帧：{(frame.HasMoreFrames ? "是" : "否")}",
            $"数据标识：{(frame.DataIdentifier.HasValue ? $"{frame.DataIdentifier:X8}H" : "-")}",
            $"传输数据域：{HexCodec.ToHex(frame.EncodedData, true)}",
            $"还原数据域：{HexCodec.ToHex(frame.Data, true)}");
}
