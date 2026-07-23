using System.Buffers;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using SuperSocket.ProtoBase;

namespace DeviceSocket.Host.Networking.Dlt645;

/// <summary>
/// 将 SuperSocket 接收缓冲区拆分为独立 DL/T 645-2007 帧。
/// </summary>
public sealed class Dlt645PipelineFilter : PipelineFilterBase<Dlt645Frame>
{
    /// <summary>
    /// 尝试读取一帧，并保留尚未完成的半包等待后续网络数据。
    /// </summary>
    public override Dlt645Frame Filter(ref SequenceReader<byte> reader)
    {
        var result = Dlt645Codec.Read(reader.UnreadSequence.ToArray());
        if (result.Status == FrameReadStatus.NeedMoreData)
            return null!;

        reader.Advance(result.Consumed);
        return result.Package!;
    }
}
