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
    /// <param name="reader">SuperSocket 为当前连接维护的未消费字节读取器。</param>
    /// <returns>完整帧可用时返回解析结果；半包时返回空且不推进读取位置。</returns>
    public override Dlt645Frame Filter(ref SequenceReader<byte> reader)
    {
        var probe = reader;
        var preambleLength = 0;
        while (preambleLength < Dlt645Codec.MaximumPreambleLength &&
               probe.TryPeek(out var preamble) &&
               preamble == 0xFE)
        {
            probe.Advance(1);
            preambleLength++;
        }

        if (!probe.TryPeek(out var firstByte))
            return null!;
        if (firstByte != 0x68)
            return ReadInvalidCandidate(ref reader);

        var headerLength = preambleLength + 10;
        var unread = reader.UnreadSequence;
        if (unread.Length < headerLength)
            return null!;

        Span<byte> header = stackalloc byte[headerLength];
        unread.Slice(0, headerLength).CopyTo(header);
        if (header[preambleLength + 7] != 0x68 ||
            header[preambleLength + 9] > Dlt645Codec.MaximumDataLength)
        {
            return ReadInvalidCandidate(ref reader);
        }

        var totalLength = preambleLength + 12 + header[preambleLength + 9];
        if (unread.Length < totalLength)
            return null!;

        // 645 帧虽然较短，仍先探测长度再复制精确帧，避免粘包时把后续报文一并复制。
        var frameBytes = unread.Slice(0, totalLength).ToArray();
        var result = Dlt645Codec.ReadCompleteFrame(frameBytes);
        if (result.Status == FrameReadStatus.NeedMoreData)
            return null!;

        reader.Advance(result.Consumed);
        return result.Package!;
    }

    /// <summary>
    /// 在前导或帧头已确定非法时沿用 Codec 的错误隔离与消费规则。
    /// </summary>
    /// <param name="reader">当前连接的未消费字节读取器。</param>
    /// <returns>可记录的协议错误帧；数据意外不足时返回空。</returns>
    private static Dlt645Frame ReadInvalidCandidate(ref SequenceReader<byte> reader)
    {
        var result = Dlt645Codec.Read(reader.UnreadSequence.ToArray());
        if (result.Status == FrameReadStatus.NeedMoreData)
            return null!;

        reader.Advance(result.Consumed);
        return result.Package!;
    }
}
