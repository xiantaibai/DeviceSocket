using System.Buffers;
using System.Buffers.Binary;
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
    /// <param name="reader">SuperSocket 为当前连接维护的未消费字节读取器。</param>
    /// <returns>完整帧可用时返回解析结果；半包时返回空且不推进读取位置。</returns>
    public override QGdw3761Frame Filter(ref SequenceReader<byte> reader)
    {
        if (!reader.TryPeek(out var firstByte))
            return null!;
        if (firstByte != 0x68)
            return ReadInvalidCandidate(ref reader);

        const int headerLength = 6;
        var unread = reader.UnreadSequence;
        if (unread.Length < headerLength)
            return null!;

        Span<byte> header = stackalloc byte[headerLength];
        unread.Slice(0, headerLength).CopyTo(header);
        var firstLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(1, 2));
        var secondLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(3, 2));
        var userDataLength = firstLength >> 2;
        if (header[5] != 0x68 ||
            firstLength != secondLength ||
            (firstLength & 0b11) != 0b10 ||
            userDataLength < 8 ||
            userDataLength > QGdw3761Codec.MaximumUserDataLength)
        {
            return ReadInvalidCandidate(ref reader);
        }

        var totalLength = userDataLength + 8;
        if (unread.Length < totalLength)
            return null!;

        // 先从 SequenceReader 读取固定头部判断目标长度，只有完整帧到齐后才复制一次精确范围；
        // 因此 4KB 分段到达的大报文不会在每个半包阶段重复复制全部累计数据。
        var frameBytes = unread.Slice(0, totalLength).ToArray();
        var result = QGdw3761Codec.ReadCompleteFrame(frameBytes);
        if (result.Status == FrameReadStatus.NeedMoreData)
            return null!;

        reader.Advance(result.Consumed);
        return result.Package!;
    }

    /// <summary>
    /// 在头部已确定非法时沿用 Codec 的异常隔离语义；该路径会消费垃圾或错误候选帧，不会造成半包重复复制。
    /// </summary>
    /// <param name="reader">当前连接的未消费字节读取器。</param>
    /// <returns>可记录的协议错误帧；数据意外不足时返回空。</returns>
    private static QGdw3761Frame ReadInvalidCandidate(ref SequenceReader<byte> reader)
    {
        var result = QGdw3761Codec.Read(reader.UnreadSequence.ToArray());
        if (result.Status == FrameReadStatus.NeedMoreData)
            return null!;

        reader.Advance(result.Consumed);
        return result.Package!;
    }
}
