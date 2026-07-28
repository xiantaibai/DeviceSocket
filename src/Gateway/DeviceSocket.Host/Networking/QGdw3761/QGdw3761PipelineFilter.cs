using System.Buffers;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;
using SuperSocket.ProtoBase;

namespace DeviceSocket.Host.Networking.QGdw3761;

/// <summary>
/// 将 SuperSocket 接收缓冲区拆分为独立 Q/GDW 376.1 帧。
/// </summary>
public sealed class QGdw3761PipelineFilter : PipelineFilterBase<QGdw3761Frame>
{
    /// <summary>
    /// 尝试读取一帧；半包返回空且不推进读取位置，完整包按解析结果消费字节。
    /// </summary>
    public override QGdw3761Frame Filter(ref SequenceReader<byte> reader)
    {
        var result = QGdw3761Codec.Read(reader.UnreadSequence.ToArray());
        if (result.Status == FrameReadStatus.NeedMoreData)
            return null!;

        reader.Advance(result.Consumed);
        return result.Package!;
    }
}
