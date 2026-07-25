using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;

namespace DeviceSocket.Debugger.Core.Decoding;

/// <summary>
/// 将通用协议枚举适配到现有 376.1 和 645 帧读取器。
/// </summary>
public static class ProtocolFrameReader
{
    /// <summary>
    /// 从缓冲区头部读取一条完整帧，并保留半包等待后续数据。
    /// </summary>
    /// <param name="protocol">当前连接使用的协议。</param>
    /// <param name="buffer">尚未消费的连续字节。</param>
    /// <returns>读取状态、消费长度和协议帧。</returns>
    public static ProtocolFrameReadResult Read(DeviceProtocol protocol, ReadOnlySpan<byte> buffer)
    {
        if (protocol == DeviceProtocol.QGdw3761)
        {
            var result = QGdw3761Codec.Read(buffer);
            return new ProtocolFrameReadResult(
                result.Status == FrameReadStatus.NeedMoreData,
                result.Consumed,
                result.Package);
        }

        var dltResult = Dlt645Codec.Read(buffer);
        return new ProtocolFrameReadResult(
            dltResult.Status == FrameReadStatus.NeedMoreData,
            dltResult.Consumed,
            dltResult.Package);
    }
}
